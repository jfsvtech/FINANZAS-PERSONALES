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
    private readonly Db _db;
    public AsistenteController(AsistenteFinancieroService asistente, WhatsAppService whatsApp, Db db) { _asistente = asistente; _whatsApp = whatsApp; _db = db; }

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
    public IActionResult GuardarNatural(string tipo, DateTime fecha, decimal monto, string descripcion, int cuentaId, int categoriaId)
    {
        if (tipo is not ("gasto" or "ingreso") || monto <= 0) return BadRequest();
        using var con = _db.Abrir();
        var valida = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM cuentas cu,categorias ca WHERE cu.id=@cuentaId AND ca.id=@categoriaId
              AND cu.usuario_id=@UsuarioId AND ca.usuario_id=@UsuarioId AND ca.tipo=@tipo", new { cuentaId, categoriaId, UsuarioId, tipo }) > 0;
        if (!valida) return Forbid();
        con.Execute(@"INSERT INTO movimientos(usuario_id,fecha,tipo,cuenta_id,categoria_id,descripcion,monto)
                      VALUES(@UsuarioId,@fecha,@tipo,@cuentaId,@categoriaId,@descripcion,@monto)",
            new { UsuarioId, fecha, tipo, cuentaId, categoriaId, descripcion, monto });
        TempData["Ok"] = "Movimiento interpretado y registrado.";
        return RedirectToAction("Index", "Movimientos", new { anio=fecha.Year, mes=fecha.Month });
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
            mensaje = string.IsNullOrWhiteSpace(r.MensajeAdmin) ? r.Mensaje : r.MensajeAdmin;
        }
        else
        {
            telefono = r.Telefono ?? "";
            mensaje = r.Mensaje;
        }

        if (string.IsNullOrWhiteSpace(telefono))
        {
            TempData["Error"] = destino == "admin"
                ? "No hay telefono administrador configurado en Integraciones."
                : "Este recordatorio no tiene telefono destino.";
            return RedirectToAction("Index");
        }

        var result = settings.PlantillaConfigurada
            ? await _whatsApp.EnviarPlantillaAsync(telefono, r.Titulo, r.Detalle, r.Fecha.ToString("dd/MM/yyyy"))
            : await _whatsApp.EnviarTextoAsync(telefono, mensaje);
        TempData[result.Ok ? "Ok" : "Error"] = result.Message;
        return RedirectToAction("Index");
    }
}
