using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class AsistenteController : BaseController
{
    private readonly AsistenteFinancieroService _asistente;
    private readonly WhatsAppService _whatsApp;
    private readonly TraduccionService _traduccion;
    private readonly Db _db;
    private readonly IWebHostEnvironment _env;
    private static readonly HashSet<string> TiposDocumentoPermitidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "application/pdf"
    };
    private const long MaxDocumentoBytes = 8 * 1024 * 1024;

    public AsistenteController(AsistenteFinancieroService asistente, WhatsAppService whatsApp, TraduccionService traduccion, Db db, IWebHostEnvironment env)
    {
        _asistente = asistente;
        _whatsApp = whatsApp;
        _traduccion = traduccion;
        _db = db;
        _env = env;
    }

    public IActionResult Index() => View(new AsistenteIndexVm
    {
        Recomendaciones = _asistente.CrearRecomendaciones(UsuarioId),
        Recordatorios = _asistente.CrearRecordatorios(UsuarioId)
    });

    public IActionResult Informe(int? anio, int? mes)
    {
        var fecha = new DateTime(anio ?? DateTime.Today.Year, mes ?? DateTime.Today.Month, 1);
        return View(_asistente.CrearInforme(UsuarioId, fecha.Year, fecha.Month));
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
        TempData["Ok"] = "Movimiento interpretado y registrado.";
        return RedirectToAction("Index", "Movimientos", new { anio=fecha.Year, mes=fecha.Month });
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
}
