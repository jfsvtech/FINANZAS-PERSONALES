using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class CierresController : BaseController
{
    private readonly Db _db;
    private readonly AsistenteFinancieroService _asistente;
    private readonly AuditoriaService _auditoria;

    public CierresController(Db db, AsistenteFinancieroService asistente, AuditoriaService auditoria)
    {
        _db = db;
        _asistente = asistente;
        _auditoria = auditoria;
    }

    public IActionResult Index(int? anio, int? mes)
    {
        var fecha = new DateTime(anio ?? DateTime.Today.Year, mes ?? DateTime.Today.Month, 1);
        using var con = _db.Abrir();
        var vm = new CierreMensualVm
        {
            Anio = fecha.Year,
            Mes = fecha.Month,
            Informe = _asistente.CrearInforme(UsuarioId, fecha.Year, fecha.Month),
            DineroSeguro = _asistente.CrearDineroSeguro(UsuarioId, fecha),
            Salud = _asistente.CalcularSaludFinanciera(UsuarioId),
            Historial = con.Query<CierreMensual>(
                @"SELECT id,usuario_id AS UsuarioId,anio,mes,ingresos,gastos_caja AS GastosCaja,
                         deuda_tarjetas AS DeudaTarjetas,saldo_por_cobrar AS SaldoPorCobrar,
                         saldo_por_pagar AS SaldoPorPagar,valor_inversiones AS ValorInversiones,
                         dinero_seguro AS DineroSeguro,salud_puntaje AS SaludPuntaje,notas,creado_en AS CreadoEn
                  FROM cierres_mensuales
                  WHERE usuario_id=@UsuarioId
                  ORDER BY anio DESC, mes DESC
                  LIMIT 18", new { UsuarioId }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int anio, int mes, string? notas)
    {
        var informe = _asistente.CrearInforme(UsuarioId, anio, mes);
        var dinero = _asistente.CrearDineroSeguro(UsuarioId, new DateTime(anio, mes, 1));
        var salud = _asistente.CalcularSaludFinanciera(UsuarioId);
        using var con = _db.Abrir();
        var id = con.ExecuteScalar<int>(
            @"INSERT INTO cierres_mensuales(usuario_id,anio,mes,ingresos,gastos_caja,deuda_tarjetas,
                   saldo_por_cobrar,saldo_por_pagar,valor_inversiones,dinero_seguro,salud_puntaje,notas)
              VALUES(@UsuarioId,@anio,@mes,@ingresos,@gastos,@deudaTarjetas,@saldoPorCobrar,@saldoPorPagar,
                   @valorInversiones,@dineroSeguro,@saludPuntaje,@notas)
              ON CONFLICT(usuario_id,anio,mes) DO UPDATE SET
                   ingresos=EXCLUDED.ingresos,
                   gastos_caja=EXCLUDED.gastos_caja,
                   deuda_tarjetas=EXCLUDED.deuda_tarjetas,
                   saldo_por_cobrar=EXCLUDED.saldo_por_cobrar,
                   saldo_por_pagar=EXCLUDED.saldo_por_pagar,
                   valor_inversiones=EXCLUDED.valor_inversiones,
                   dinero_seguro=EXCLUDED.dinero_seguro,
                   salud_puntaje=EXCLUDED.salud_puntaje,
                   notas=EXCLUDED.notas,
                   creado_en=NOW()
              RETURNING id",
            new
            {
                UsuarioId,
                anio,
                mes,
                ingresos = informe.Ingresos,
                gastos = informe.Gastos,
                deudaTarjetas = informe.DeudaTarjetas,
                saldoPorCobrar = informe.SaldoPorCobrar,
                saldoPorPagar = informe.SaldoPorPagar,
                valorInversiones = informe.ValorInversiones,
                dineroSeguro = dinero.Valor,
                saludPuntaje = salud.Puntaje,
                notas = (notas ?? "").Trim()
            });
        _auditoria.Registrar(UsuarioId, UsuarioId, "Finanzas", "Cierre mensual", "cierres_mensuales", id, $"Cierre {anio}-{mes:00} guardado.");
        TempData["Ok"] = "Cierre mensual guardado. Ya tienes una foto fija para comparar despues.";
        return RedirectToAction("Index", new { anio, mes });
    }
}
