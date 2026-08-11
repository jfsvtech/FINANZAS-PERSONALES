using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace FinanzasPersonales.Web.Controllers;

public class AsistenteController : BaseController
{
    private readonly AsistenteFinancieroService _asistente;
    private readonly WhatsAppService _whatsApp;
    private readonly TraduccionService _traduccion;
    private readonly Db _db;
    private readonly IWebHostEnvironment _env;
    private readonly EmailService _email;
    private readonly AuditoriaService _auditoria;
    private static readonly HashSet<string> TiposDocumentoPermitidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "application/pdf"
    };
    private const long MaxDocumentoBytes = 8 * 1024 * 1024;

    public AsistenteController(AsistenteFinancieroService asistente, WhatsAppService whatsApp, TraduccionService traduccion, Db db, IWebHostEnvironment env, EmailService email, AuditoriaService auditoria)
    {
        _asistente = asistente;
        _whatsApp = whatsApp;
        _traduccion = traduccion;
        _db = db;
        _env = env;
        _email = email;
        _auditoria = auditoria;
    }

    public IActionResult Index() => View(CrearVm());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Preguntar(string pregunta)
    {
        var vm = CrearVm();
        vm.Pregunta = pregunta?.Trim() ?? "";
        vm.Respuesta = _asistente.ResponderPregunta(UsuarioId, vm.Pregunta);
        _auditoria.Registrar(UsuarioId, UsuarioId, "Asistente", "Pregunta financiera", "asistente", null, vm.Pregunta);
        return View("Index", vm);
    }

    public IActionResult Informe(int? anio, int? mes)
    {
        var fecha = new DateTime(anio ?? DateTime.Today.Year, mes ?? DateTime.Today.Month, 1);
        return View(_asistente.CrearInforme(UsuarioId, fecha.Year, fecha.Month));
    }

    public IActionResult InformePdf(int? anio, int? mes)
    {
        var fecha = new DateTime(anio ?? DateTime.Today.Year, mes ?? DateTime.Today.Month, 1);
        var vm = _asistente.CrearInforme(UsuarioId, fecha.Year, fecha.Month);
        var pdf = CrearPdfBasico(vm);
        _auditoria.Registrar(UsuarioId, UsuarioId, "Asistente", "Descargar informe PDF", "informe_mensual", null, $"Informe {vm.Periodo} descargado.");
        return File(pdf, "application/pdf", $"Informe financiero {vm.Anio}-{vm.Mes:00}.pdf");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnviarInforme(int anio, int mes)
    {
        var vm = _asistente.CrearInforme(UsuarioId, anio, mes);
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "Tu usuario no tiene correo asociado.";
            return RedirectToAction("Informe", new { anio, mes });
        }
        var ok = await _email.EnviarAsync(email, $"Informe mensual - {vm.Periodo}", CrearHtmlInforme(vm));
        _auditoria.Registrar(UsuarioId, UsuarioId, "Asistente", "Enviar informe", "informe_mensual", null, $"Informe {vm.Periodo} enviado por correo.");
        TempData[ok ? "Ok" : "Error"] = ok ? "Informe mensual enviado a tu correo." : "No se pudo enviar el informe.";
        return RedirectToAction("Informe", new { anio, mes });
    }

    public IActionResult Registro() => View(_asistente.Interpretar(UsuarioId, ""));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Interpretar(string texto) => View("Registro", _asistente.Interpretar(UsuarioId, texto));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InterpretarDocumento(IFormFile? archivoDocumento, string textoOcr, bool guardarImagenOriginal = false)
    {
        if (archivoDocumento is { Length: > 0 })
        {
            if (archivoDocumento.Length > MaxDocumentoBytes)
            {
                TempData["Error"] = "El documento supera el limite permitido de 8 MB.";
                return View("Registro", _asistente.Interpretar(UsuarioId, ""));
            }
            if (!TiposDocumentoPermitidos.Contains(archivoDocumento.ContentType))
            {
                TempData["Error"] = "Formato no permitido. Usa JPG, PNG, WEBP o PDF.";
                return View("Registro", _asistente.Interpretar(UsuarioId, ""));
            }
        }

        var vm = await _asistente.InterpretarDocumentoAsync(UsuarioId, textoOcr, archivoDocumento?.FileName);
        string? rutaRelativa = null;
        var imagenGuardada = false;
        if (guardarImagenOriginal && archivoDocumento is { Length: > 0 } && archivoDocumento.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            rutaRelativa = await GuardarArchivoDocumentoAsync(archivoDocumento);
            imagenGuardada = true;
        }

        if (!string.IsNullOrWhiteSpace(vm.TextoOcr))
        {
            vm.GuardarImagenOriginal = guardarImagenOriginal;
            vm.ImagenGuardada = imagenGuardada;
            vm.DocumentoOcrId = _asistente.RegistrarAuditoriaDocumento(
                UsuarioId,
                vm.TextoOcr,
                archivoDocumento?.FileName,
                archivoDocumento?.ContentType,
                archivoDocumento?.Length ?? 0,
                imagenGuardada,
                rutaRelativa,
                vm.ProveedorOcr,
                vm.ProveedorIa,
                vm.Confianza,
                null);
        }

        return View("Registro", vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarNatural(string tipo, DateTime fecha, decimal monto, string descripcion, int cuentaId, int categoriaId, int? documentoOcrId = null)
    {
        if (tipo is not ("gasto" or "ingreso") || monto <= 0) return BadRequest();
        using var con = _db.Abrir();
        var valida = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM cuentas cu,categorias ca WHERE cu.id=@cuentaId AND ca.id=@categoriaId
              AND cu.usuario_id=@UsuarioId AND ca.usuario_id=@UsuarioId AND ca.tipo=@tipo", new { cuentaId, categoriaId, UsuarioId, tipo }) > 0;
        if (!valida) return Forbid();
        var monedaBase = con.ExecuteScalar<string?>("SELECT moneda_codigo FROM usuarios WHERE id=@UsuarioId", new { UsuarioId }) ?? "COP";
        var movimientoId = con.ExecuteScalar<int>(@"INSERT INTO movimientos(usuario_id,fecha,tipo,cuenta_id,categoria_id,descripcion,monto,monto_original,moneda_codigo,tasa_conversion,moneda_base_codigo)
                      VALUES(@UsuarioId,@fecha,@tipo,@cuentaId,@categoriaId,@descripcion,@monto,@monto,@monedaBase,1,@monedaBase)
                      RETURNING id",
            new { UsuarioId, fecha, tipo, cuentaId, categoriaId, descripcion, monto, monedaBase });
        if (documentoOcrId.HasValue)
        {
            _asistente.AsociarDocumentoMovimiento(UsuarioId, documentoOcrId.Value, movimientoId);
        }
        _auditoria.Registrar(UsuarioId, UsuarioId, "Asistente", "Registro natural", "movimientos", movimientoId, $"Movimiento {tipo} por {monto:N0} registrado desde asistente.");
        TempData["Ok"] = "Movimiento interpretado y registrado.";
        return RedirectToAction("Index", "Movimientos", new { anio=fecha.Year, mes=fecha.Month });
    }

    private static string CrearHtmlInforme(InformeMensualVm vm)
    {
        var recomendaciones = string.Join("", vm.Recomendaciones.Select(r => $"<li><strong>{System.Net.WebUtility.HtmlEncode(r.Titulo)}</strong>: {System.Net.WebUtility.HtmlEncode(r.Detalle)}</li>"));
        return $"""
        <div style="font-family:Arial,sans-serif;max-width:720px;margin:auto;padding:24px;background:#F5F5F7;color:#1C1C1E">
          <div style="background:#fff;border-radius:18px;border:1px solid #E5E7EB;padding:28px">
            <div style="color:#7C3AED;font-weight:800;text-transform:uppercase;letter-spacing:.08em;font-size:12px">Informe mensual</div>
            <h2>{System.Net.WebUtility.HtmlEncode(vm.Periodo)}</h2>
            <p><strong>Ingresos:</strong> {vm.Ingresos:C0} · <strong>Gastos:</strong> {vm.Gastos:C0} · <strong>Resultado:</strong> {vm.Balance:C0}</p>
            <p><strong>Por cobrar:</strong> {vm.SaldoPorCobrar:C0} · <strong>Por pagar:</strong> {vm.SaldoPorPagar:C0} · <strong>Inversiones:</strong> {vm.ValorInversiones:C0}</p>
            <p><strong>Patrimonio operativo:</strong> {vm.PatrimonioOperativo:C0}</p>
            <h3>Recomendaciones</h3>
            <ul>{recomendaciones}</ul>
          </div>
        </div>
        """;
    }

    private static byte[] CrearPdfBasico(InformeMensualVm vm)
    {
        var lineas = new[]
        {
            $"Informe financiero mensual - {vm.Periodo}",
            $"Ingresos: {vm.Ingresos:C0}",
            $"Gastos: {vm.Gastos:C0}",
            $"Resultado: {vm.Balance:C0}",
            $"Tasa de ahorro: {vm.TasaAhorro}%",
            $"Deuda tarjetas: {vm.DeudaTarjetas:C0}",
            $"Saldo por cobrar: {vm.SaldoPorCobrar:C0}",
            $"Saldo por pagar: {vm.SaldoPorPagar:C0}",
            $"Valor inversiones: {vm.ValorInversiones:C0}",
            $"Patrimonio operativo: {vm.PatrimonioOperativo:C0}",
            "",
            "Recomendaciones:",
        }.Concat(vm.Recomendaciones.Take(8).Select(x => "- " + x.Titulo + ": " + x.Detalle)).ToList();
        var text = string.Join("\\n", lineas).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var stream = $"BT /F1 11 Tf 50 780 Td 14 TL ({text}) Tj ET";
        stream = stream.Replace("\\n", ") Tj T* (");
        var pdf = $"""
%PDF-1.4
1 0 obj <</Type /Catalog /Pages 2 0 R>> endobj
2 0 obj <</Type /Pages /Kids [3 0 R] /Count 1>> endobj
3 0 obj <</Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources <</Font <</F1 4 0 R>>>> /Contents 5 0 R>> endobj
4 0 obj <</Type /Font /Subtype /Type1 /BaseFont /Helvetica>> endobj
5 0 obj <</Length {Encoding.ASCII.GetByteCount(stream)}>> stream
{stream}
endstream endobj
xref
0 6
0000000000 65535 f 
trailer <</Root 1 0 R /Size 6>>
startxref
0
%%EOF
""";
        return Encoding.ASCII.GetBytes(pdf);
    }

    private async Task<string> GuardarArchivoDocumentoAsync(IFormFile archivo)
    {
        var basePath = Path.Combine(_env.ContentRootPath, "App_Data", "DocumentosOcr", UsuarioId.ToString());
        Directory.CreateDirectory(basePath);
        var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) extension = ".bin";
        var nombreSeguro = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{extension}";
        var ruta = Path.Combine(basePath, nombreSeguro);
        await using var stream = System.IO.File.Create(ruta);
        await archivo.CopyToAsync(stream);
        return Path.Combine("App_Data", "DocumentosOcr", UsuarioId.ToString(), nombreSeguro).Replace('\\', '/');
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnviarWhatsAppRecordatorio(int indice, string destino)
    {
        var recordatorios = _asistente.CrearRecordatorios(UsuarioId);
        if (indice < 0 || indice >= recordatorios.Count)
        {
            TempData["Error"] = "Recordatorio no encontrado.";
            return RedirectToAction("Index");
        }

        var r = recordatorios[indice];
        var settings = _whatsApp.ObtenerConfiguracion();
        string telefono;
        string mensaje;
        if (destino == "admin")
        {
            telefono = settings.AdminPhone;
            mensaje = _traduccion.T(string.IsNullOrWhiteSpace(r.MensajeAdmin) ? r.Mensaje : r.MensajeAdmin, User);
        }
        else
        {
            telefono = r.Telefono ?? "";
            mensaje = _traduccion.T(r.Mensaje, User);
        }

        if (string.IsNullOrWhiteSpace(telefono))
        {
            TempData["Error"] = destino == "admin"
                ? "No hay telefono administrador configurado en Integraciones."
                : "Este recordatorio no tiene telefono destino.";
            return RedirectToAction("Index");
        }

        var result = settings.PlantillaConfigurada
            ? await _whatsApp.EnviarPlantillaAsync(telefono, _traduccion.T(r.Titulo, User), _traduccion.T(r.Detalle, User), r.Fecha.ToString("dd/MM/yyyy"))
            : await _whatsApp.EnviarTextoAsync(telefono, mensaje);
        TempData[result.Ok ? "Ok" : "Error"] = result.Message;
        return RedirectToAction("Index");
    }

    private AsistenteIndexVm CrearVm() => new()
    {
        Recomendaciones = _asistente.CrearRecomendaciones(UsuarioId),
        Recordatorios = _asistente.CrearRecordatorios(UsuarioId),
        DineroSeguro = _asistente.CrearDineroSeguro(UsuarioId),
        Salud = _asistente.CalcularSaludFinanciera(UsuarioId),
        Radar = _asistente.CrearRadar(UsuarioId)
    };
}
