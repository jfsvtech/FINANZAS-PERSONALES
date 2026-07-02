using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class DirectivoController : BaseController
{
    private readonly Db _db;
    public DirectivoController(Db db) => _db = db;

    public IActionResult Index()
    {
        var hoy = DateTime.Today;
        var desde = new DateTime(hoy.Year, hoy.Month, 1);
        var hasta = desde.AddMonths(1);
        using var con = _db.Abrir();
        var vm = new DirectivoVm();
        var p = new { usuarioId = UsuarioId, desde, hasta };
        vm.IngresosMes = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        vm.GastosMes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                    WHEN m.tipo='gasto' AND c.tipo<>'tarjeta_credito' THEN m.monto
                    WHEN m.tipo='pago_tarjeta' THEN m.monto
                    ELSE 0 END)
              FROM movimientos m
              JOIN cuentas c ON c.id=m.cuenta_id
              WHERE m.usuario_id=@usuarioId AND m.fecha>=@desde AND m.fecha<@hasta
                AND m.tipo IN ('gasto','pago_tarjeta')", p) ?? 0;
        vm.Liquidez = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                    WHEN m.tipo='ingreso' AND c.tipo<>'tarjeta_credito' THEN m.monto
                    WHEN m.tipo='gasto' AND c.tipo<>'tarjeta_credito' THEN -m.monto
                    WHEN m.tipo='pago_tarjeta' THEN -m.monto ELSE 0 END)
              FROM movimientos m JOIN cuentas c ON c.id=m.cuenta_id
              WHERE m.usuario_id=@usuarioId", new { usuarioId = UsuarioId }) ?? 0;
        vm.DeudaTarjetas = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE WHEN m.tipo='gasto' AND c.tipo='tarjeta_credito' THEN m.monto
                             WHEN m.tipo='pago_tarjeta' THEN -m.monto ELSE 0 END)
              FROM movimientos m LEFT JOIN cuentas c ON c.id=m.cuenta_id WHERE m.usuario_id=@usuarioId",
            new { usuarioId = UsuarioId }) ?? 0;
        vm.MetasAhorradas = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM aportes_meta WHERE usuario_id=@usuarioId", new { usuarioId = UsuarioId }) ?? 0;
        vm.ValorInversiones = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(GREATEST(0,
                    COALESCE(v.valor,i.capital_inicial) +
                    COALESCE((SELECT SUM(CASE WHEN m.tipo IN ('aporte','rendimiento') THEN m.monto ELSE -m.monto END)
                              FROM inversion_movimientos m
                              WHERE m.inversion_id=i.id AND (v.fecha IS NULL OR m.fecha>v.fecha)),0)))
              FROM inversiones i
              LEFT JOIN LATERAL (
                  SELECT valor,fecha FROM inversion_valoraciones
                  WHERE inversion_id=i.id ORDER BY fecha DESC,id DESC LIMIT 1
              ) v ON TRUE
              WHERE i.usuario_id=@usuarioId AND i.estado='activa'",
            new { usuarioId = UsuarioId }) ?? 0;

        var prestamos = con.Query<Prestamo>(@"SELECT id,capital,tasa_mensual AS TasaMensual,fecha,estado FROM prestamos WHERE usuario_id=@usuarioId AND estado='activo'", new { usuarioId = UsuarioId }).ToList();
        var pagos = con.Query<PrestamoPago>("SELECT id,prestamo_id AS PrestamoId,fecha,tipo,monto FROM prestamo_pagos WHERE usuario_id=@usuarioId", new { usuarioId = UsuarioId }).ToLookup(x => x.PrestamoId);
        foreach (var pr in prestamos) CalculoPrestamos.CompletarCalculos(pr, pagos[pr.Id].ToList());
        vm.SaldoPrestamos = prestamos.Sum(x => x.SaldoCapital);
        vm.InteresPendiente = prestamos.Sum(x => x.InteresPendiente);
        vm.SerieMensual = con.Query<SerieMesVm>(
            @"SELECT EXTRACT(YEAR FROM fecha)::int AS Anio,EXTRACT(MONTH FROM fecha)::int AS Mes,
              SUM(CASE WHEN m.tipo='ingreso' THEN m.monto ELSE 0 END) AS Ingresos,
              SUM(CASE
                  WHEN m.tipo='gasto' AND c.tipo<>'tarjeta_credito' THEN m.monto
                  WHEN m.tipo='pago_tarjeta' THEN m.monto
                  ELSE 0 END) AS Gastos
              FROM movimientos m
              JOIN cuentas c ON c.id=m.cuenta_id
              WHERE m.usuario_id=@usuarioId AND m.fecha>=@inicio AND m.tipo IN ('ingreso','gasto','pago_tarjeta')
              GROUP BY 1,2 ORDER BY 1,2", new { usuarioId = UsuarioId, inicio = desde.AddMonths(-11) }).ToList();
        vm.Composicion = new()
        {
            new() { Nombre="Liquidez", Total=Math.Max(0,vm.Liquidez), Color="#7C3AED" },
            new() { Nombre="Prestamos por cobrar", Total=vm.SaldoPrestamos, Color="#D4AF37" },
            new() { Nombre="Inversiones", Total=vm.ValorInversiones, Color="#A78BFA" },
            new() { Nombre="Metas registradas", Total=vm.MetasAhorradas, Color="#A78BFA" }
        };
        return View(vm);
    }
}
