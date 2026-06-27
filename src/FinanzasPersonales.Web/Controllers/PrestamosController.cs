using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class PrestamosController : BaseController
{
    private readonly Db _db;
    private readonly PreferenciasUsuarioService _preferencias;

    public PrestamosController(Db db, PreferenciasUsuarioService preferencias)
    {
        _db = db;
        _preferencias = preferencias;
    }

    public IActionResult Index(int? personaId, string? estado, DateTime? desde, DateTime? hasta)
    {
        (desde, hasta) = NormalizarRango(desde, hasta);
        using var con = _db.Abrir();
        var pref = _preferencias.Obtener(UsuarioId);
        var vm = new PrestamosIndexVm
        {
            FiltroPersonaId = personaId,
            FiltroEstado = estado,
            FiltroDesde = desde,
            FiltroHasta = hasta,
            Monedas = _preferencias.Monedas(),
            MonedaBase = pref.MonedaCodigo,
            Prestamos = ConsultarPrestamos(con, personaId: personaId, estado: estado, desde: desde, hasta: hasta),
            Personas = con.Query<Persona>(
                @"SELECT id, nombre FROM personas WHERE usuario_id=@UsuarioId AND activo ORDER BY nombre",
                new { UsuarioId }).ToList()
        };

        var pagosPorPrestamo = ConsultarPagos(con).ToLookup(x => x.PrestamoId);
        foreach (var p in vm.Prestamos)
            CalculoPrestamos.CompletarCalculos(p, pagosPorPrestamo[p.Id].ToList());

        return View(vm);
    }

    public IActionResult Tablero(int? personaId, DateTime? desde, DateTime? hasta)
    {
        (desde, hasta) = NormalizarRango(desde, hasta);
        using var con = _db.Abrir();
        var vm = new PrestamosTableroVm
        {
            FiltroPersonaId = personaId,
            FiltroDesde = desde,
            FiltroHasta = hasta,
            Prestamos = ConsultarPrestamos(con, personaId: personaId),
            Personas = con.Query<Persona>(
                @"SELECT id, nombre FROM personas WHERE usuario_id=@UsuarioId AND activo ORDER BY nombre",
                new { UsuarioId }).ToList()
        };

        var idsIncluidos = vm.Prestamos.Select(p => p.Id).ToHashSet();
        var pagos = ConsultarPagos(con).Where(x => idsIncluidos.Contains(x.PrestamoId)).ToList();
        var pagosPorPrestamo = pagos.ToLookup(x => x.PrestamoId);
        foreach (var p in vm.Prestamos)
            CalculoPrestamos.CompletarCalculos(p, pagosPorPrestamo[p.Id].ToList());

        var inicioSerie = desde?.Date ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11);
        var finSerie = hasta?.Date ?? DateTime.Today;
        vm.CobrosPorMes = pagos
            .Where(x => x.Fecha.Date >= inicioSerie && x.Fecha.Date <= finSerie)
            .GroupBy(x => new { x.Fecha.Year, x.Fecha.Month })
            .Select(g => new CobroMesVm
            {
                Anio = g.Key.Year,
                Mes = g.Key.Month,
                Intereses = g.Where(x => x.Tipo == "pago_interes").Sum(x => x.Monto),
                Capital = g.Where(x => x.Tipo == "abono_capital").Sum(x => x.Monto)
            })
            .OrderBy(x => x.Anio).ThenBy(x => x.Mes).ToList();

        var paleta = new[] { "#0d6efd", "#fb8c00", "#2e7d32", "#d81b60", "#5e35b1", "#00897b", "#c62828", "#3949ab", "#fdd835", "#8e24aa" };
        vm.SaldoPorPersona = vm.Activos
            .GroupBy(p => p.PersonaNombre ?? "?")
            .Select(g => new GastoCategoriaVm { Nombre = g.Key, Total = g.Sum(x => x.SaldoCapital) })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total).ToList();
        for (var i = 0; i < vm.SaldoPorPersona.Count; i++)
            vm.SaldoPorPersona[i].Color = paleta[i % paleta.Length];

        return View(vm);
    }

    public IActionResult Detalle(int id)
    {
        using var con = _db.Abrir();
        var prestamo = ConsultarPrestamos(con, id).FirstOrDefault();
        if (prestamo == null) return NotFound();

        var pagos = ConsultarPagos(con, id);
        CalculoPrestamos.CompletarCalculos(prestamo, pagos);
        var pref = _preferencias.Obtener(UsuarioId);
        return View(new PrestamoDetalleVm { Prestamo = prestamo, Pagos = pagos, Monedas = _preferencias.Monedas(), MonedaBase = pref.MonedaCodigo });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, int personaId, DateTime fecha, decimal capital, decimal tasaMensual,
        int? diaPagoInteres, DateTime? fechaPagoCapital, string? notas, string monedaCodigo = "COP", decimal? tasaConversion = null)
    {
        if (capital <= 0 || tasaMensual < 0)
        {
            TempData["Error"] = "Capital y tasa deben ser validos.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        var personaMia = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM personas WHERE id=@personaId AND usuario_id=@UsuarioId",
            new { personaId, UsuarioId }) > 0;
        if (!personaMia)
        {
            TempData["Error"] = "Primero crea la persona en el modulo de Personas.";
            return RedirectToAction("Index");
        }

        ConversionMoneda conversion;
        try { conversion = Convertir(capital, monedaCodigo, fecha, tasaConversion); }
        catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToAction(id == 0 ? "Index" : "Detalle", id == 0 ? null : new { id }); }
        if (id == 0)
        {
            var nuevoId = con.ExecuteScalar<int>(
                @"INSERT INTO prestamos (usuario_id, persona_id, fecha, capital, capital_original, moneda_codigo, tasa_conversion,
                         tasa_mensual, dia_pago_interes, fecha_pago_capital, notas)
                  VALUES (@UsuarioId, @personaId, @fecha, @capitalBase, @capitalOriginal, @monedaCodigo, @tasa,
                         @tasaMensual, @diaPagoInteres, @fechaPagoCapital, @notas)
                  RETURNING id",
                new { UsuarioId, personaId, fecha, capitalBase = conversion.MontoBase, capitalOriginal = conversion.MontoOriginal, monedaCodigo = conversion.MonedaOrigen, tasa = conversion.Tasa, tasaMensual, diaPagoInteres, fechaPagoCapital, notas });
            TempData["Ok"] = "Prestamo registrado.";
            return RedirectToAction("Detalle", new { id = nuevoId });
        }

        con.Execute(
            @"UPDATE prestamos SET persona_id=@personaId, fecha=@fecha, capital=@capitalBase, capital_original=@capitalOriginal,
                     moneda_codigo=@monedaCodigo, tasa_conversion=@tasa, tasa_mensual=@tasaMensual,
                     dia_pago_interes=@diaPagoInteres, fecha_pago_capital=@fechaPagoCapital, notas=@notas
              WHERE id=@id AND usuario_id=@UsuarioId",
            new { id, UsuarioId, personaId, fecha, capitalBase = conversion.MontoBase, capitalOriginal = conversion.MontoOriginal, monedaCodigo = conversion.MonedaOrigen, tasa = conversion.Tasa, tasaMensual, diaPagoInteres, fechaPagoCapital, notas });
        ActualizarEstado(con, id);
        TempData["Ok"] = "Prestamo actualizado.";
        return RedirectToAction("Detalle", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RegistrarPago(int prestamoId, DateTime fecha, string tipo, decimal monto, string? notas, string monedaCodigo = "COP", decimal? tasaConversion = null)
    {
        if (monto <= 0 || tipo is not ("abono_capital" or "pago_interes")) return BadRequest();

        using var con = _db.Abrir();
        var prestamo = ConsultarPrestamos(con, prestamoId).FirstOrDefault();
        if (prestamo == null) return NotFound();

        if (fecha.Date < prestamo.Fecha.Date)
        {
            TempData["Error"] = "La fecha del pago no puede ser anterior a la fecha del prestamo.";
            return RedirectToAction("Detalle", new { id = prestamoId });
        }

        ConversionMoneda conversion;
        try { conversion = Convertir(monto, monedaCodigo, fecha, tasaConversion); }
        catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToAction("Detalle", new { id = prestamoId }); }
        if (tipo == "abono_capital")
        {
            var abonado = con.ExecuteScalar<decimal?>(
                @"SELECT SUM(monto) FROM prestamo_pagos
                  WHERE prestamo_id=@prestamoId AND usuario_id=@UsuarioId AND tipo='abono_capital'",
                new { prestamoId, UsuarioId }) ?? 0;
            var saldo = prestamo.Capital - abonado;
            if (conversion.MontoBase > saldo)
            {
                TempData["Error"] = $"El abono ({conversion.MontoBase:C0}) supera el saldo de capital ({saldo:C0}).";
                return RedirectToAction("Detalle", new { id = prestamoId });
            }
        }

        con.Execute(
            @"INSERT INTO prestamo_pagos (usuario_id, prestamo_id, fecha, tipo, monto, monto_original, moneda_codigo, tasa_conversion, moneda_base_codigo, notas)
              VALUES (@UsuarioId, @prestamoId, @fecha, @tipo, @montoBase, @montoOriginal, @monedaCodigo, @tasa, @monedaBase, @notas)",
            new { UsuarioId, prestamoId, fecha, tipo, montoBase = conversion.MontoBase, montoOriginal = conversion.MontoOriginal, monedaCodigo = conversion.MonedaOrigen, tasa = conversion.Tasa, monedaBase = conversion.MonedaDestino, notas });

        ActualizarEstado(con, prestamoId);
        TempData["Ok"] = tipo == "abono_capital" ? "Abono a capital registrado." : "Pago de interes registrado.";
        return RedirectToAction("Detalle", new { id = prestamoId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EliminarPago(int id, int prestamoId)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM prestamo_pagos WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        ActualizarEstado(con, prestamoId);
        TempData["Ok"] = "Pago eliminado del historial.";
        return RedirectToAction("Detalle", new { id = prestamoId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM prestamo_pagos WHERE prestamo_id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        con.Execute("DELETE FROM prestamos WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Prestamo eliminado con todo su historial.";
        return RedirectToAction("Index");
    }

    private static (DateTime? Desde, DateTime? Hasta) NormalizarRango(DateTime? desde, DateTime? hasta)
    {
        desde = desde?.Date;
        hasta = hasta?.Date;
        return desde.HasValue && hasta.HasValue && hasta < desde ? (hasta, desde) : (desde, hasta);
    }

    private List<Prestamo> ConsultarPrestamos(System.Data.IDbConnection con, int? id = null,
        int? personaId = null, string? estado = null, DateTime? desde = null, DateTime? hasta = null)
    {
        var sql = @"SELECT p.id, p.usuario_id AS UsuarioId, p.persona_id AS PersonaId, p.fecha, p.capital,
                           p.capital_original AS CapitalOriginal,
                           COALESCE(p.moneda_codigo,'COP') AS MonedaCodigo,
                           COALESCE(p.tasa_conversion,1) AS TasaConversion,
                           p.tasa_mensual AS TasaMensual, p.dia_pago_interes AS DiaPagoInteres,
                           p.fecha_pago_capital AS FechaPagoCapital, p.notas, p.estado,
                           pe.nombre AS PersonaNombre
                    FROM prestamos p JOIN personas pe ON pe.id = p.persona_id
                    WHERE p.usuario_id=@UsuarioId";
        if (id.HasValue) sql += " AND p.id=@id";
        if (personaId.HasValue) sql += " AND p.persona_id=@personaId";
        if (estado is "activo" or "pagado") sql += " AND p.estado=@estado";
        if (desde.HasValue) sql += " AND p.fecha >= @desde";
        if (hasta.HasValue) sql += " AND p.fecha <= @hasta";
        sql += " ORDER BY p.estado, p.fecha DESC";
        return con.Query<Prestamo>(sql, new { UsuarioId, id, personaId, estado, desde, hasta }).ToList();
    }

    private List<PrestamoPago> ConsultarPagos(System.Data.IDbConnection con, int? prestamoId = null)
    {
        var sql = @"SELECT id, usuario_id AS UsuarioId, prestamo_id AS PrestamoId, fecha, tipo, monto,
                           COALESCE(monto_original,monto) AS MontoOriginal,
                           COALESCE(moneda_codigo,'COP') AS MonedaCodigo,
                           COALESCE(tasa_conversion,1) AS TasaConversion,
                           COALESCE(moneda_base_codigo,'COP') AS MonedaBaseCodigo,
                           notas
                    FROM prestamo_pagos WHERE usuario_id=@UsuarioId";
        if (prestamoId.HasValue) sql += " AND prestamo_id=@prestamoId";
        sql += " ORDER BY fecha DESC, id DESC";
        return con.Query<PrestamoPago>(sql, new { UsuarioId, prestamoId }).ToList();
    }

    private void ActualizarEstado(System.Data.IDbConnection con, int prestamoId)
    {
        con.Execute(
            @"UPDATE prestamos SET estado = CASE
                  WHEN capital <= COALESCE((SELECT SUM(monto) FROM prestamo_pagos
                       WHERE prestamo_id=@prestamoId AND tipo='abono_capital'),0)
                  THEN 'pagado' ELSE 'activo' END
              WHERE id=@prestamoId AND usuario_id=@UsuarioId",
            new { prestamoId, UsuarioId });
    }

    private ConversionMoneda Convertir(decimal monto, string monedaCodigo, DateTime fecha, decimal? tasaConversion)
    {
        var pref = _preferencias.Obtener(UsuarioId);
        try
        {
            return _preferencias.ConvertirAsync(monto, monedaCodigo, pref.MonedaCodigo, fecha, tasaConversion)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }
}
