using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class CalendarioController : BaseController
{
    private readonly Db _db;
    public CalendarioController(Db db) => _db = db;

    public IActionResult Index(int? anio, int? mes)
    {
        var hoy = DateTime.Today;
        var fecha = new DateTime(anio ?? hoy.Year, mes ?? hoy.Month, 1);
        using var con = _db.Abrir();
        var vm = new CalendarioFinancieroVm { Anio = fecha.Year, Mes = fecha.Month };
        vm.EventosMes = CrearEventos(con, fecha, fecha.AddMonths(1).AddDays(-1));
        vm.ProximosEventos = CrearEventos(con, hoy, hoy.AddDays(60))
            .Where(x => x.Fecha.Date >= hoy).OrderBy(x => x.Fecha).Take(30).ToList();
        return View(vm);
    }

    private List<EventoFinancieroVm> CrearEventos(System.Data.IDbConnection con, DateTime desde, DateTime hasta)
    {
        var eventos = new List<EventoFinancieroVm>();

        var tarjetas = con.Query<Cuenta>(
            @"SELECT c.id,c.nombre,c.tipo,c.icono,c.dia_pago AS DiaPago,
              COALESCE((SELECT SUM(CASE WHEN m.tipo='gasto' AND m.cuenta_id=c.id THEN m.monto
                                       WHEN m.tipo='pago_tarjeta' AND m.cuenta_destino_id=c.id THEN -m.monto ELSE 0 END)
                        FROM movimientos m WHERE m.usuario_id=@UsuarioId),0) AS Saldo
              FROM cuentas c WHERE c.usuario_id=@UsuarioId AND c.activo
              AND c.tipo='tarjeta_credito' AND c.dia_pago IS NOT NULL", new { UsuarioId }).ToList();
        foreach (var tarjeta in tarjetas)
            foreach (var fecha in FechasMensuales(desde, hasta, tarjeta.DiaPago!.Value))
                eventos.Add(new()
                {
                    Fecha=fecha, Tipo="tarjeta", Icono=tarjeta.Icono, Titulo=$"Pago tarjeta: {tarjeta.Nombre}",
                    Detalle="Fecha habitual de pago", Monto=Math.Max(0,tarjeta.Saldo),
                    Controller="Movimientos", Action="Index", AccionTexto="Registrar pago", Color="#D34A56"
                });

        eventos.AddRange(con.Query<GastoPeriodico>(
            @"SELECT g.id,g.tipo,g.nombre,g.monto_estimado AS MontoEstimado,g.proxima_fecha AS ProximaFecha,
              c.icono AS CategoriaIcono FROM gastos_periodicos g JOIN categorias c ON c.id=g.categoria_id
              WHERE g.usuario_id=@UsuarioId AND g.activo AND g.proxima_fecha BETWEEN @desde AND @hasta",
            new { UsuarioId, desde, hasta }).Select(x => new EventoFinancieroVm
            {
                Fecha=x.ProximaFecha,Tipo="periodico",Icono=x.CategoriaIcono,Titulo=x.Nombre,
                Detalle=x.Tipo == "ingreso" ? "Ingreso recurrente" : "Gasto periodico",
                Monto=x.MontoEstimado,Controller="Periodicos",Action="Index",
                RouteId=x.Id,AccionTexto=x.Tipo == "ingreso" ? "Registrar ingreso" : "Registrar gasto",
                Color=x.Tipo == "ingreso" ? "#2F9E64" : "#D4AF37"
            }));

        var prestamos = con.Query<Prestamo>(
            @"SELECT p.id,p.fecha,p.capital,p.tasa_mensual AS TasaMensual,p.dia_pago_interes AS DiaPagoInteres,
              p.fecha_pago_capital AS FechaPagoCapital,p.estado,pe.nombre AS PersonaNombre
              FROM prestamos p JOIN personas pe ON pe.id=p.persona_id
              WHERE p.usuario_id=@UsuarioId AND p.estado='activo'", new { UsuarioId }).ToList();
        var pagos = con.Query<PrestamoPago>(
            @"SELECT id,prestamo_id AS PrestamoId,fecha,tipo,monto
              FROM prestamo_pagos WHERE usuario_id=@UsuarioId", new { UsuarioId }).ToLookup(x => x.PrestamoId);
        foreach (var prestamo in prestamos)
        {
            CalculoPrestamos.CompletarCalculos(prestamo, pagos[prestamo.Id].ToList());
            if (prestamo.DiaPagoInteres.HasValue)
                foreach (var fecha in FechasMensuales(desde, hasta, prestamo.DiaPagoInteres.Value))
                    eventos.Add(new()
                    {
                        Fecha=fecha,Tipo="prestamo",Icono="bi-cash-stack",Titulo=$"Cobro a {prestamo.PersonaNombre}",
                        Detalle="Interes mensual del prestamo",Monto=prestamo.InteresPendiente > 0 ? prestamo.InteresPendiente : prestamo.InteresMensualActual,
                        Controller="Prestamos",Action="Detalle",RouteId=prestamo.Id,AccionTexto="Registrar cobro",Color="#7C3AED"
                    });
            if (prestamo.FechaPagoCapital.HasValue && prestamo.FechaPagoCapital.Value.Date >= desde.Date && prestamo.FechaPagoCapital.Value.Date <= hasta.Date)
                eventos.Add(new()
                {
                    Fecha=prestamo.FechaPagoCapital.Value,Tipo="prestamo",Icono="bi-cash-stack",
                    Titulo=$"Capital: {prestamo.PersonaNombre}",Detalle="Fecha pactada para capital",Monto=prestamo.SaldoCapital,
                    Controller="Prestamos",Action="Detalle",RouteId=prestamo.Id,AccionTexto="Registrar abono",Color="#A78BFA"
                });
        }

        eventos.AddRange(con.Query<MetaAhorro>(
            @"SELECT m.id,m.nombre,m.monto_objetivo AS MontoObjetivo,m.fecha_objetivo AS FechaObjetivo,m.color,
              COALESCE((SELECT SUM(a.monto) FROM aportes_meta a WHERE a.meta_ahorro_id=m.id),0) AS Ahorrado
              FROM metas_ahorro m WHERE m.usuario_id=@UsuarioId AND m.activo
              AND m.fecha_objetivo BETWEEN @desde AND @hasta", new { UsuarioId, desde, hasta })
            .Where(x => x.Restante > 0).Select(x => new EventoFinancieroVm
            {
                Fecha=x.FechaObjetivo!.Value,Tipo="meta",Icono="bi-flag",Titulo=$"Meta: {x.Nombre}",
                Detalle="Fecha objetivo",Monto=x.Restante,Controller="Metas",Action="Index",
                RouteId=x.Id,AccionTexto="Registrar aporte",Color=x.Color
            }));

        eventos.AddRange(con.Query<Inversion>(
            @"SELECT id,nombre,fecha_retorno AS FechaRetorno,color,icono,capital_inicial AS CapitalInicial
              FROM inversiones WHERE usuario_id=@UsuarioId AND estado='activa'
              AND fecha_retorno BETWEEN @desde AND @hasta", new { UsuarioId, desde, hasta })
            .Select(x => new EventoFinancieroVm
            {
                Fecha=x.FechaRetorno!.Value,Tipo="inversion",Icono=x.Icono,Titulo=$"Retorno: {x.Nombre}",
                Detalle="Fecha esperada de retorno",Monto=x.CapitalInicial,Controller="Inversiones",Action="Detalle",
                RouteId=x.Id,AccionTexto="Registrar rendimiento",Color=x.Color
            }));
        foreach (var evento in eventos)
            evento.Url = evento.Tipo switch
            {
                "tarjeta" => Url.Action("Index", "Movimientos", new { nuevo=true, tipo="pago_tarjeta" }) ?? "",
                "periodico" => Url.Action("Index", "Periodicos", new { registrar=evento.RouteId }) ?? "",
                "prestamo" => Url.Action("Detalle", "Prestamos", new { id=evento.RouteId, accion=evento.Detalle.Contains("capital",StringComparison.OrdinalIgnoreCase) ? "capital" : "interes" }) ?? "",
                "meta" => Url.Action("Index", "Metas", new { aporte=evento.RouteId }) ?? "",
                "inversion" => Url.Action("Detalle", "Inversiones", new { id=evento.RouteId, accion="rendimiento" }) ?? "",
                _ => Url.Action(evento.Action, evento.Controller, new { id=evento.RouteId }) ?? ""
            };
        return eventos.OrderBy(x => x.Fecha).ThenBy(x => x.Titulo).ToList();
    }

    private static IEnumerable<DateTime> FechasMensuales(DateTime desde, DateTime hasta, int dia)
    {
        var cursor = new DateTime(desde.Year, desde.Month, 1);
        var fin = new DateTime(hasta.Year, hasta.Month, 1);
        while (cursor <= fin)
        {
            var fecha = new DateTime(cursor.Year, cursor.Month, Math.Min(dia, DateTime.DaysInMonth(cursor.Year, cursor.Month)));
            if (fecha.Date >= desde.Date && fecha.Date <= hasta.Date) yield return fecha;
            cursor = cursor.AddMonths(1);
        }
    }
}
