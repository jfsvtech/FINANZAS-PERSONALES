using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class DashboardController : BaseController
{
    private readonly Db _db;
    public DashboardController(Db db) => _db = db;

    public IActionResult Index(int? anio, int? mes)
    {
        var hoy = DateTime.Today;
        var a = anio ?? hoy.Year;
        var m = mes ?? hoy.Month;
        var desde = new DateTime(a, m, 1);
        var hasta = desde.AddMonths(1);
        var desdeAnterior = desde.AddMonths(-1);

        using var con = _db.Abrir();
        var vm = new DashboardVm { Anio = a, Mes = m };

        var p = new { usuarioId = UsuarioId, desde, hasta };

        vm.IngresosMes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;

        vm.GastosMes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        vm.IngresosMesAnterior = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desdeAnterior AND fecha<@desde",
            new { usuarioId = UsuarioId, desdeAnterior, desde }) ?? 0;
        vm.GastosMesAnterior = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@desdeAnterior AND fecha<@desde",
            new { usuarioId = UsuarioId, desdeAnterior, desde }) ?? 0;

        vm.PagosTarjetaMes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto) FROM movimientos
              WHERE usuario_id=@usuarioId AND tipo='pago_tarjeta' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;

        vm.SaldoAnterior = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE WHEN tipo='ingreso' THEN monto WHEN tipo='gasto' THEN -monto ELSE 0 END)
              FROM movimientos
              WHERE usuario_id=@usuarioId AND fecha<@desde AND tipo IN ('ingreso','gasto')", p) ?? 0;
        vm.IncluirSaldoAnterior = con.ExecuteScalar<bool?>(
            "SELECT incluir_saldo_anterior FROM configuraciones_usuario WHERE usuario_id=@usuarioId",
            new { usuarioId = UsuarioId }) ?? false;

        // Saldos por cuenta (historico completo, no solo del mes).
        // efectivo/debito: ingresos - gastos - pagos de tarjeta hechos desde la cuenta
        // tarjeta_credito: deuda = gastos con la tarjeta - pagos recibidos
        vm.Cuentas = con.Query<Cuenta>(
            @"SELECT c.id, c.usuario_id AS UsuarioId, c.nombre, c.tipo, c.icono, c.dia_pago AS DiaPago, c.activo,
                     COALESCE((SELECT SUM(CASE
                          WHEN m.tipo='ingreso'      AND m.cuenta_id=c.id         THEN  m.monto
                          WHEN m.tipo='gasto'        AND m.cuenta_id=c.id         THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_id=c.id         THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_destino_id=c.id THEN  m.monto
                          ELSE 0 END)
                        FROM movimientos m
                        WHERE m.usuario_id=@usuarioId AND (m.cuenta_id=c.id OR m.cuenta_destino_id=c.id)),0) AS Saldo
              FROM cuentas c
              WHERE c.usuario_id=@usuarioId AND c.activo
              ORDER BY c.tipo, c.nombre",
            new { usuarioId = UsuarioId }).ToList();

        // Para tarjetas el saldo calculado queda negativo (mas gastos que pagos): lo
        // presentamos como deuda positiva.
        foreach (var c in vm.Cuentas.Where(c => c.Tipo == "tarjeta_credito"))
            c.Saldo = -c.Saldo;
        vm.DeudaTarjetas = vm.Cuentas.Where(c => c.Tipo == "tarjeta_credito").Sum(c => c.Saldo);

        vm.GastosPorCategoria = con.Query<GastoCategoriaVm>(
            @"SELECT cat.nombre, cat.color, cat.icono, SUM(m.monto) AS Total
              FROM movimientos m JOIN categorias cat ON cat.id = m.categoria_id
              WHERE m.usuario_id=@usuarioId AND m.tipo='gasto' AND m.fecha>=@desde AND m.fecha<@hasta
              GROUP BY cat.nombre, cat.color, cat.icono
              ORDER BY Total DESC", p).ToList();
        vm.ComparacionCategorias = con.Query<ComparacionCategoriaVm>(
            @"SELECT cat.nombre, cat.color, cat.icono,
                     COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@desde AND m.fecha<@hasta),0) AS Actual,
                     COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@desdeAnterior AND m.fecha<@desde),0) AS Anterior
              FROM categorias cat
              LEFT JOIN movimientos m ON m.categoria_id=cat.id AND m.usuario_id=@usuarioId AND m.tipo='gasto'
                   AND m.fecha>=@desdeAnterior AND m.fecha<@hasta
              WHERE cat.usuario_id=@usuarioId AND cat.tipo='gasto'
              GROUP BY cat.id, cat.nombre, cat.color, cat.icono
              HAVING COALESCE(SUM(m.monto),0)>0
              ORDER BY (COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@desde AND m.fecha<@hasta),0)
                      - COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@desdeAnterior AND m.fecha<@desde),0)) DESC",
            new { usuarioId = UsuarioId, desdeAnterior, desde, hasta }).ToList();
        vm.GastosFijos = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(m.monto) FROM movimientos m JOIN categorias c ON c.id=m.categoria_id
              WHERE m.usuario_id=@usuarioId AND m.tipo='gasto' AND c.clase='fijo' AND m.fecha>=@desde AND m.fecha<@hasta", p) ?? 0;
        vm.GastosVariables = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(m.monto) FROM movimientos m JOIN categorias c ON c.id=m.categoria_id
              WHERE m.usuario_id=@usuarioId AND m.tipo='gasto' AND c.clase<>'fijo' AND m.fecha>=@desde AND m.fecha<@hasta", p) ?? 0;
        vm.FlujoDiario = con.Query<FlujoDiaVm>(
            @"SELECT EXTRACT(DAY FROM fecha)::int AS Dia,
                     SUM(SUM(CASE WHEN tipo='ingreso' THEN monto WHEN tipo='gasto' THEN -monto ELSE 0 END))
                     OVER (ORDER BY EXTRACT(DAY FROM fecha)) AS Balance
              FROM movimientos
              WHERE usuario_id=@usuarioId AND fecha>=@desde AND fecha<@hasta AND tipo IN ('ingreso','gasto')
              GROUP BY fecha ORDER BY fecha", p).ToList();

        // Ultimos 12 meses: ingresos vs gastos
        var inicioSerie = new DateTime(a, m, 1).AddMonths(-11);
        vm.SerieMensual = con.Query<SerieMesVm>(
            @"SELECT EXTRACT(YEAR FROM fecha)::int AS Anio, EXTRACT(MONTH FROM fecha)::int AS Mes,
                     SUM(CASE WHEN tipo='ingreso' THEN monto ELSE 0 END) AS Ingresos,
                     SUM(CASE WHEN tipo='gasto'   THEN monto ELSE 0 END) AS Gastos
              FROM movimientos
              WHERE usuario_id=@usuarioId AND fecha>=@inicioSerie AND fecha<@hasta AND tipo IN ('ingreso','gasto')
              GROUP BY 1,2 ORDER BY 1,2",
            new { usuarioId = UsuarioId, inicioSerie, hasta }).ToList();

        // Presupuestos del mes con lo gastado
        vm.Presupuestos = con.Query<Presupuesto>(
            @"SELECT p.id, p.usuario_id AS UsuarioId, p.categoria_id AS CategoriaId, p.monto_mensual AS MontoMensual,
                     cat.nombre AS CategoriaNombre, cat.color AS CategoriaColor, cat.icono AS CategoriaIcono,
                     COALESCE((SELECT SUM(m.monto) FROM movimientos m
                               WHERE m.usuario_id=@usuarioId AND m.tipo='gasto'
                                 AND m.categoria_id=p.categoria_id
                                 AND m.fecha>=@desde AND m.fecha<@hasta),0) AS Gastado
              FROM presupuestos p JOIN categorias cat ON cat.id=p.categoria_id
              WHERE p.usuario_id=@usuarioId
              ORDER BY cat.nombre", p).ToList();

        // Gastos periodicos pendientes hasta fin del mes consultado
        vm.PeriodicosPendientes = con.Query<GastoPeriodico>(
            @"SELECT g.id, g.usuario_id AS UsuarioId, g.tipo, g.nombre, g.categoria_id AS CategoriaId,
                     g.cuenta_id AS CuentaId, g.monto_estimado AS MontoEstimado,
                     g.frecuencia_meses AS FrecuenciaMeses, g.proxima_fecha AS ProximaFecha, g.activo,
                     cat.nombre AS CategoriaNombre, cat.icono AS CategoriaIcono, cu.nombre AS CuentaNombre
              FROM gastos_periodicos g
              JOIN categorias cat ON cat.id=g.categoria_id
              LEFT JOIN cuentas cu ON cu.id=g.cuenta_id
              WHERE g.usuario_id=@usuarioId AND g.activo AND g.proxima_fecha < @hasta
              ORDER BY g.proxima_fecha",
            new { usuarioId = UsuarioId, hasta }).ToList();

        foreach (var g in vm.PeriodicosPendientes.Take(3))
            vm.Alertas.Add(new AlertaFinancieraVm
            {
                Tipo = g.ProximaFecha.Date <= DateTime.Today ? "danger" : "warning",
                Icono = g.CategoriaIcono,
                Titulo = g.ProximaFecha.Date <= DateTime.Today
                    ? $"{(g.Tipo == "ingreso" ? "Ingreso" : "Pago")} pendiente: {g.Nombre}"
                    : $"Proximo {(g.Tipo == "ingreso" ? "ingreso" : "pago")}: {g.Nombre}",
                Detalle = $"{g.MontoEstimado:C0} · {g.ProximaFecha:dd MMM}",
                Controller = "Periodicos"
            });
        foreach (var pr in vm.Presupuestos.Where(x => x.MontoMensual > 0 && x.Gastado * 100 / x.MontoMensual >= 80).Take(3))
            vm.Alertas.Add(new AlertaFinancieraVm
            {
                Tipo = pr.Gastado >= pr.MontoMensual ? "danger" : "warning",
                Icono = pr.CategoriaIcono,
                Titulo = pr.Gastado >= pr.MontoMensual ? $"Presupuesto superado: {pr.CategoriaNombre}" : $"Presupuesto cerca del límite: {pr.CategoriaNombre}",
                Detalle = $"{pr.Gastado:C0} de {pr.MontoMensual:C0}",
                Controller = "Presupuestos"
            });
        var metasProximas = con.Query<MetaAhorro>(
            @"SELECT m.id,m.nombre,m.monto_objetivo AS MontoObjetivo,m.fecha_objetivo AS FechaObjetivo,
                     COALESCE((SELECT SUM(a.monto) FROM aportes_meta a WHERE a.meta_ahorro_id=m.id),0) AS Ahorrado
              FROM metas_ahorro m WHERE m.usuario_id=@usuarioId AND m.activo AND m.fecha_objetivo IS NOT NULL
                AND m.fecha_objetivo<=@limite",
            new { usuarioId = UsuarioId, limite = DateTime.Today.AddDays(45) }).ToList();
        foreach (var meta in metasProximas.Where(x => x.Restante > 0).Take(2))
            vm.Alertas.Add(new AlertaFinancieraVm
            {
                Tipo = "info", Icono = "bi-flag", Titulo = $"Meta próxima: {meta.Nombre}",
                Detalle = $"Faltan {meta.Restante:C0} · fecha {meta.FechaObjetivo:dd MMM}", Controller = "Metas"
            });

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarConfiguracionSaldo(bool incluirSaldoAnterior, int anio, int mes)
    {
        using var con = _db.Abrir();
        con.Execute(
            @"INSERT INTO configuraciones_usuario (usuario_id, incluir_saldo_anterior)
              VALUES (@UsuarioId, @incluirSaldoAnterior)
              ON CONFLICT (usuario_id) DO UPDATE SET incluir_saldo_anterior=@incluirSaldoAnterior",
            new { UsuarioId, incluirSaldoAnterior });
        TempData["Ok"] = incluirSaldoAnterior
            ? "El disponible mensual ahora incluye el saldo acumulado anterior."
            : "El disponible mensual ahora muestra solo el resultado del mes.";
        return RedirectToAction("Index", new { anio, mes });
    }
}
