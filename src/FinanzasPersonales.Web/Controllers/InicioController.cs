using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

// Portal de bienvenida: resumen rapido de los dos modulos.
public class InicioController : BaseController
{
    private readonly Db _db;
    public InicioController(Db db) => _db = db;

    public IActionResult Index()
    {
        var hoy = DateTime.Today;
        var desde = new DateTime(hoy.Year, hoy.Month, 1);
        var hasta = desde.AddMonths(1);

        using var con = _db.Abrir();
        var vm = new InicioVm();
        var p = new { usuarioId = UsuarioId, desde, hasta };

        vm.IngresosMes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        vm.GastosMes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        vm.SaldoAnterior = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE WHEN tipo='ingreso' THEN monto WHEN tipo='gasto' THEN -monto ELSE 0 END)
              FROM movimientos
              WHERE usuario_id=@usuarioId AND fecha<@desde AND tipo IN ('ingreso','gasto')", p) ?? 0;
        vm.IncluirSaldoAnterior = con.ExecuteScalar<bool?>(
            "SELECT incluir_saldo_anterior FROM configuraciones_usuario WHERE usuario_id=@usuarioId",
            new { usuarioId = UsuarioId }) ?? false;

        vm.DeudaTarjetas = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                  WHEN m.tipo='gasto' THEN m.monto
                  WHEN m.tipo='pago_tarjeta' THEN -m.monto
                  ELSE 0 END)
              FROM movimientos m
              JOIN cuentas c ON c.id = COALESCE(m.cuenta_destino_id, m.cuenta_id)
              WHERE m.usuario_id=@usuarioId AND c.tipo='tarjeta_credito'
                AND ((m.tipo='gasto' AND m.cuenta_id=c.id) OR (m.tipo='pago_tarjeta' AND m.cuenta_destino_id=c.id))",
            new { usuarioId = UsuarioId }) ?? 0;
        if (vm.DeudaTarjetas < 0) vm.DeudaTarjetas = 0;

        vm.PeriodicosPendientes = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM gastos_periodicos
              WHERE usuario_id=@usuarioId AND activo AND proxima_fecha < @hasta",
            new { usuarioId = UsuarioId, hasta });
        vm.CuentasActivas = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM cuentas WHERE usuario_id=@usuarioId AND activo",
            new { usuarioId = UsuarioId });
        vm.CategoriasActivas = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM categorias WHERE usuario_id=@usuarioId AND activo",
            new { usuarioId = UsuarioId });
        vm.MetasActivas = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM metas_ahorro WHERE usuario_id=@usuarioId AND activo",
            new { usuarioId = UsuarioId });
        vm.AlertasPendientes = vm.PeriodicosPendientes
            + con.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM presupuestos pr
                  WHERE pr.usuario_id=@usuarioId AND
                    COALESCE((SELECT SUM(m.monto) FROM movimientos m WHERE m.usuario_id=@usuarioId
                      AND m.tipo='gasto' AND m.categoria_id=pr.categoria_id AND m.fecha>=@desde AND m.fecha<@hasta),0)
                    >= pr.monto_mensual * .8",
                p);

        // Resumen de prestamos
        var prestamos = con.Query<Prestamo>(
            @"SELECT p.id, p.usuario_id AS UsuarioId, p.persona_id AS PersonaId, p.fecha, p.capital,
                     p.tasa_mensual AS TasaMensual, p.estado
              FROM prestamos p WHERE p.usuario_id=@usuarioId AND p.estado='activo'",
            new { usuarioId = UsuarioId }).ToList();
        var pagos = con.Query<PrestamoPago>(
                @"SELECT id, prestamo_id AS PrestamoId, fecha, tipo, monto
                  FROM prestamo_pagos WHERE usuario_id=@usuarioId", new { usuarioId = UsuarioId })
            .ToLookup(x => x.PrestamoId);
        foreach (var pr in prestamos)
            CalculoPrestamos.CompletarCalculos(pr, pagos[pr.Id].ToList());

        vm.PrestamosActivos = prestamos.Count;
        vm.SaldoPorCobrar = prestamos.Sum(x => x.SaldoCapital);
        vm.InteresPendiente = prestamos.Sum(x => x.InteresPendiente);
        vm.InteresMensualEsperado = prestamos.Sum(x => x.InteresMensualActual);
        vm.PrestamosAtrasados = prestamos.Count(x => x.InteresMensualActual > 0 && x.InteresPendiente >= x.InteresMensualActual);

        vm.InversionesActivas = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM inversiones WHERE usuario_id=@usuarioId AND estado='activa'",
            new { usuarioId = UsuarioId });
        vm.CapitalInversiones = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(i.capital_inicial + COALESCE((SELECT SUM(m.monto) FROM inversion_movimientos m
              WHERE m.inversion_id=i.id AND m.tipo='aporte'),0))
              FROM inversiones i WHERE i.usuario_id=@usuarioId AND i.estado='activa'",
            new { usuarioId = UsuarioId }) ?? 0;
        vm.ValorInversiones = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(GREATEST(0,COALESCE(v.valor,i.capital_inicial) +
              COALESCE((SELECT SUM(CASE WHEN m.tipo IN ('aporte','rendimiento') THEN m.monto ELSE -m.monto END)
              FROM inversion_movimientos m WHERE m.inversion_id=i.id AND (v.fecha IS NULL OR m.fecha>v.fecha)),0)))
              FROM inversiones i LEFT JOIN LATERAL (
              SELECT valor,fecha FROM inversion_valoraciones WHERE inversion_id=i.id ORDER BY fecha DESC,id DESC LIMIT 1
              ) v ON TRUE WHERE i.usuario_id=@usuarioId AND i.estado='activa'",
            new { usuarioId = UsuarioId }) ?? 0;
        var retirosInversiones = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(m.monto) FROM inversion_movimientos m JOIN inversiones i ON i.id=m.inversion_id
              WHERE i.usuario_id=@usuarioId AND i.estado='activa' AND m.tipo='retiro'",
            new { usuarioId = UsuarioId }) ?? 0;
        vm.GananciaInversiones = vm.ValorInversiones + retirosInversiones - vm.CapitalInversiones;
        vm.RetornosProximos = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM inversiones WHERE usuario_id=@usuarioId AND estado='activa'
              AND fecha_retorno BETWEEN CURRENT_DATE AND CURRENT_DATE + 60",
            new { usuarioId = UsuarioId });

        return View(vm);
    }
}
