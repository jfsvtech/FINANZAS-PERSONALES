using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class OnboardingController : BaseController
{
    private readonly Db _db;
    private readonly AuditoriaService _auditoria;

    public OnboardingController(Db db, AuditoriaService auditoria)
    {
        _db = db;
        _auditoria = auditoria;
    }

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        return View(CrearVm(con));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarPreferencias(string idioma = "es", string monedaCodigo = "COP",
        bool recordatoriosEmailActivos = true, int recordatoriosEmailDiasAntes = 3)
    {
        idioma = PreferenciasUsuarioService.NormalizarIdioma(idioma);
        monedaCodigo = PreferenciasUsuarioService.NormalizarMoneda(monedaCodigo);
        recordatoriosEmailDiasAntes = Math.Clamp(recordatoriosEmailDiasAntes, 0, 60);
        using var con = _db.Abrir();
        con.Execute("UPDATE usuarios SET idioma=@idioma, moneda_codigo=@monedaCodigo WHERE id=@UsuarioId",
            new { UsuarioId, idioma, monedaCodigo });
        con.Execute(
            @"INSERT INTO configuraciones_usuario(usuario_id,incluir_saldo_anterior,recordatorios_email_activos,recordatorios_email_dias_antes)
              VALUES(@UsuarioId,FALSE,@recordatoriosEmailActivos,@recordatoriosEmailDiasAntes)
              ON CONFLICT(usuario_id) DO UPDATE
              SET recordatorios_email_activos=@recordatoriosEmailActivos,
                  recordatorios_email_dias_antes=@recordatoriosEmailDiasAntes",
            new { UsuarioId, recordatoriosEmailActivos, recordatoriosEmailDiasAntes });
        _auditoria.Registrar(UsuarioId, UsuarioId, "Onboarding", "Preferencias", "usuarios", UsuarioId, "Preferencias iniciales configuradas.");
        TempData["Ok"] = "Preferencias iniciales guardadas.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CrearBase()
    {
        using var con = _db.Abrir();
        var cuentas = con.ExecuteScalar<int>("SELECT COUNT(*) FROM cuentas WHERE usuario_id=@UsuarioId", new { UsuarioId });
        if (cuentas == 0)
        {
            con.Execute(
                @"INSERT INTO cuentas(usuario_id,nombre,tipo,icono) VALUES
                  (@UsuarioId,'Efectivo','efectivo','bi-cash-coin'),
                  (@UsuarioId,'Cuenta principal','debito','bi-bank'),
                  (@UsuarioId,'Tarjeta de credito','tarjeta_credito','bi-credit-card')",
                new { UsuarioId });
        }

        var categorias = con.ExecuteScalar<int>("SELECT COUNT(*) FROM categorias WHERE usuario_id=@UsuarioId", new { UsuarioId });
        if (categorias == 0)
        {
            con.Execute(
                @"INSERT INTO categorias(usuario_id,nombre,tipo,clase,color,icono) VALUES
                  (@UsuarioId,'Salario','ingreso','fijo','#22C55E','bi-briefcase'),
                  (@UsuarioId,'Otros ingresos','ingreso','variable','#2F9E64','bi-plus-circle'),
                  (@UsuarioId,'Mercado','gasto','variable','#F59F00','bi-basket'),
                  (@UsuarioId,'Transporte','gasto','variable','#4C6EF5','bi-car-front'),
                  (@UsuarioId,'Servicios','gasto','fijo','#7C3AED','bi-lightning-charge'),
                  (@UsuarioId,'Restaurantes','gasto','variable','#E8590C','bi-cup-hot'),
                  (@UsuarioId,'Salud','gasto','variable','#E15B64','bi-heart-pulse'),
                  (@UsuarioId,'Otros gastos','gasto','variable','#6B7280','bi-three-dots')",
                new { UsuarioId });
        }

        _auditoria.Registrar(UsuarioId, UsuarioId, "Onboarding", "Crear base", "usuarios", UsuarioId, "Cuentas y categorias iniciales creadas.");
        TempData["Ok"] = "Base inicial creada. Ya puedes registrar tu primer movimiento.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Finalizar()
    {
        using var con = _db.Abrir();
        con.Execute("UPDATE usuarios SET onboarding_completado=TRUE WHERE id=@UsuarioId", new { UsuarioId });
        _auditoria.Registrar(UsuarioId, UsuarioId, "Onboarding", "Finalizar", "usuarios", UsuarioId, "Onboarding completado.");
        TempData["Ok"] = "Configuracion inicial completada.";
        return RedirectToAction("Index", "Inicio");
    }

    private OnboardingVm CrearVm(System.Data.IDbConnection con)
    {
        var pref = con.QueryFirstOrDefault<(string Idioma, string MonedaCodigo)>(
            "SELECT idioma AS Idioma, moneda_codigo AS MonedaCodigo FROM usuarios WHERE id=@UsuarioId", new { UsuarioId });
        var rec = con.QueryFirstOrDefault<(bool? Activos, int? Dias)>(
            "SELECT recordatorios_email_activos AS Activos, recordatorios_email_dias_antes AS Dias FROM configuraciones_usuario WHERE usuario_id=@UsuarioId",
            new { UsuarioId });
        return new OnboardingVm
        {
            TieneCuentas = con.ExecuteScalar<int>("SELECT COUNT(*) FROM cuentas WHERE usuario_id=@UsuarioId", new { UsuarioId }) > 0,
            TieneCategorias = con.ExecuteScalar<int>("SELECT COUNT(*) FROM categorias WHERE usuario_id=@UsuarioId", new { UsuarioId }) > 0,
            TieneMovimiento = con.ExecuteScalar<int>("SELECT COUNT(*) FROM movimientos WHERE usuario_id=@UsuarioId", new { UsuarioId }) > 0,
            Idioma = pref.Idioma ?? "es",
            MonedaCodigo = pref.MonedaCodigo ?? "COP",
            RecordatoriosEmailActivos = rec.Activos ?? true,
            RecordatoriosEmailDiasAntes = Math.Clamp(rec.Dias ?? 3, 0, 60)
        };
    }
}
