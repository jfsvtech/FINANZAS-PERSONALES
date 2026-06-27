using System.Security.Claims;
using System.Globalization;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FinanzasPersonales.Web.Controllers;

[Authorize]
public abstract class BaseController : Controller
{
    // Identidad del usuario autenticado: TODA consulta filtra por este id,
    // de modo que cada usuario solo ve su propia informacion.
    protected int UsuarioId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    protected bool EsAdmin => User.HasClaim("EsAdmin", "true");

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        AplicarCulturaUsuario();
        var controller = context.RouteData.Values["controller"]?.ToString() ?? "";
        var permiso = PermisoRequerido(controller);
        if (!string.IsNullOrWhiteSpace(permiso) && !EsAdmin && !User.HasClaim(permiso, "true"))
        {
            TempData["Error"] = "No tienes permiso para acceder a este modulo. Contacta al administrador de la suscripcion.";
            context.Result = RedirectToAction("Index", "Inicio");
            return;
        }

        base.OnActionExecuting(context);
    }

    protected bool TienePermiso(string permiso) => EsAdmin || User.HasClaim(permiso, "true");

    private void AplicarCulturaUsuario()
    {
        var idioma = User.FindFirstValue("Idioma") ?? "es";
        var monedaCodigo = PreferenciasUsuarioService.NormalizarMoneda(User.FindFirstValue("MonedaCodigo"));
        var culturaCodigo = idioma switch
        {
            "en" => "en-US",
            "pt" => "pt-BR",
            _ => "es-CO"
        };
        var cultura = (CultureInfo)CultureInfo.GetCultureInfo(culturaCodigo).Clone();
        var moneda = PreferenciasUsuarioService.MonedasSoportadas.FirstOrDefault(x => x.Codigo == monedaCodigo)
            ?? PreferenciasUsuarioService.MonedasSoportadas[0];
        cultura.NumberFormat.CurrencySymbol = moneda.Simbolo;
        cultura.NumberFormat.CurrencyDecimalDigits = moneda.Decimales;
        CultureInfo.CurrentCulture = cultura;
        CultureInfo.CurrentUICulture = cultura;
    }

    private static string PermisoRequerido(string controller) => controller switch
    {
        "Dashboard" or "Movimientos" or "Importar" or "Periodicos" or "Presupuestos" or "Metas" or "Cuentas" or "Categorias" => "PermisoGastos",
        "Prestamos" or "Personas" => "PermisoPrestamos",
        "Inversiones" or "TiposInversion" => "PermisoInversiones",
        "Directivo" => "PermisoDirectivo",
        "Asistente" => "PermisoAsistente",
        "Calendario" => "PermisoCalendario",
        "Usuarios" or "Configuracion" => "EsAdmin",
        "Documentacion" => "",
        _ => ""
    };
}
