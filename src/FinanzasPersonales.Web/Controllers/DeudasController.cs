using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class DeudasController : BaseController
{
    private readonly Db _db;
    private readonly PreferenciasUsuarioService _preferencias;

    public DeudasController(Db db, PreferenciasUsuarioService preferencias)
    {
        _db = db;
        _preferencias = preferencias;
    }

    public IActionResult Index(string? estado, string? tipo)
    {
        estado = estado is "activa" or "pagada" or "refinanciada" or "vencida" ? estado : null;
        tipo = NormalizarTipo(tipo, permitirVacio: true);
        using var con = _db.Abrir();
        var pref = _preferencias.Obtener(UsuarioId);
        var deudas = ConsultarDeudas(con, estado: estado, tipo: tipo);
        var pagos = ConsultarPagos(con).ToLookup(x => x.DeudaId);
        foreach (var deuda in deudas) CompletarCalculos(deuda, pagos[deuda.Id]);
        return View(new DeudasIndexVm
        {
            Deudas = deudas,
            FiltroEstado = estado,
            FiltroTipo = tipo,
            Cuentas = CuentasActivas(con),
            Monedas = _preferencias.Monedas(),
            MonedaBase = pref.MonedaCodigo
        });
    }

    public IActionResult Detalle(int id, string? accion)
    {
        using var con = _db.Abrir();
        var deuda = ConsultarDeudas(con, id: id).FirstOrDefault();
        if (deuda == null) return NotFound();
        var pagos = ConsultarPagos(con, id);
        CompletarCalculos(deuda, pagos);
        var pref = _preferencias.Obtener(UsuarioId);
        ViewBag.AccionCalendario = accion ?? "";
        return View(new DeudaDetalleVm
        {
            Deuda = deuda,
            Pagos = pagos,
            Cuentas = CuentasActivas(con),
            Monedas = _preferencias.Monedas(),
            MonedaBase = pref.MonedaCodigo
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string acreedor, string tipo, DateTime fechaDesembolso,
        decimal capitalInicial, decimal tasa, string periodoTasa, string sistemaPago, int plazoMeses,
        int? diaPago, DateTime? proximaFechaPago, decimal? cuotaEstimada, int? cuentaDesembolsoId,
        int? cuentaPagoId, string? notas, string monedaCodigo = "COP", decimal? tasaConversion = null)
    {
        if (string.IsNullOrWhiteSpace(acreedor) || capitalInicial <= 0 || tasa < 0)
        {
            TempData["Error"] = "Acreedor, capital y tasa deben ser validos.";
            return RedirectToAction("Index");
        }

        tipo = NormalizarTipo(tipo);
        periodoTasa = periodoTasa == "anual" ? "anual" : "mensual";
        sistemaPago = NormalizarSistema(sistemaPago);
        if (sistemaPago == "sin_interes")
        {
            tasa = 0;
            diaPago = null;
        }
        else if (diaPago is < 1 or > 31)
        {
            TempData["Error"] = "El dia de pago debe estar entre 1 y 31.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        if (cuentaDesembolsoId.HasValue && !CuentaEsMia(con, cuentaDesembolsoId.Value)) return Forbid();
        if (cuentaPagoId.HasValue && !CuentaEsMia(con, cuentaPagoId.Value)) return Forbid();

        ConversionMoneda conversion;
        try { conversion = Convertir(capitalInicial, monedaCodigo, fechaDesembolso, tasaConversion); }
        catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToAction(id == 0 ? "Index" : "Detalle", id == 0 ? null : new { id }); }

        if (id == 0)
        {
            var nuevoId = con.ExecuteScalar<int>(
                @"INSERT INTO deudas(usuario_id,acreedor,tipo,fecha_desembolso,capital_inicial,capital_original,
                         moneda_codigo,tasa_conversion,moneda_base_codigo,tasa,periodo_tasa,sistema_pago,plazo_meses,
                         dia_pago,proxima_fecha_pago,cuota_estimada,cuenta_desembolso_id,cuenta_pago_id,notas)
                  VALUES(@UsuarioId,@acreedor,@tipo,@fechaDesembolso,@capitalBase,@capitalOriginal,@monedaCodigo,
                         @tasaConv,@monedaBase,@tasa,@periodoTasa,@sistemaPago,@plazoMeses,@diaPago,@proximaFechaPago,
                         @cuotaEstimada,@cuentaDesembolsoId,@cuentaPagoId,@notas)
                  RETURNING id",
                new
                {
                    UsuarioId,
                    acreedor = acreedor.Trim(),
                    tipo,
                    fechaDesembolso,
                    capitalBase = conversion.MontoBase,
                    capitalOriginal = conversion.MontoOriginal,
                    monedaCodigo = conversion.MonedaOrigen,
                    tasaConv = conversion.Tasa,
                    monedaBase = conversion.MonedaDestino,
                    tasa,
                    periodoTasa,
                    sistemaPago,
                    plazoMeses,
                    diaPago,
                    proximaFechaPago,
                    cuotaEstimada,
                    cuentaDesembolsoId,
                    cuentaPagoId,
                    notas
                });
            TempData["Ok"] = "Deuda registrada.";
            return RedirectToAction("Detalle", new { id = nuevoId });
        }

        con.Execute(
            @"UPDATE deudas SET acreedor=@acreedor,tipo=@tipo,fecha_desembolso=@fechaDesembolso,
                     capital_inicial=@capitalBase,capital_original=@capitalOriginal,moneda_codigo=@monedaCodigo,
                     tasa_conversion=@tasaConv,moneda_base_codigo=@monedaBase,tasa=@tasa,periodo_tasa=@periodoTasa,
                     sistema_pago=@sistemaPago,plazo_meses=@plazoMeses,dia_pago=@diaPago,
                     proxima_fecha_pago=@proximaFechaPago,cuota_estimada=@cuotaEstimada,
                     cuenta_desembolso_id=@cuentaDesembolsoId,cuenta_pago_id=@cuentaPagoId,notas=@notas
              WHERE id=@id AND usuario_id=@UsuarioId",
            new
            {
                id,
                UsuarioId,
                acreedor = acreedor.Trim(),
                tipo,
                fechaDesembolso,
                capitalBase = conversion.MontoBase,
                capitalOriginal = conversion.MontoOriginal,
                monedaCodigo = conversion.MonedaOrigen,
                tasaConv = conversion.Tasa,
                monedaBase = conversion.MonedaDestino,
                tasa,
                periodoTasa,
                sistemaPago,
                plazoMeses,
                diaPago,
                proximaFechaPago,
                cuotaEstimada,
                cuentaDesembolsoId,
                cuentaPagoId,
                notas
            });
        ActualizarEstado(con, id);
        TempData["Ok"] = "Deuda actualizada.";
        return RedirectToAction("Detalle", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RegistrarPago(int deudaId, DateTime fecha, decimal montoTotal, decimal capital,
        decimal interes, decimal costos, int? cuentaPagoId, string? notas, string efectoAbono = "no_aplica",
        bool esExtraordinario = false, string monedaCodigo = "COP", decimal? tasaConversion = null)
    {
        if (montoTotal <= 0 || capital < 0 || interes < 0 || costos < 0)
        {
            TempData["Error"] = "Los valores del pago no son validos.";
            return RedirectToAction("Detalle", new { id = deudaId });
        }
        if (capital + interes + costos <= 0)
        {
            TempData["Error"] = "Distribuye el pago entre capital, interes o costos.";
            return RedirectToAction("Detalle", new { id = deudaId });
        }

        using var con = _db.Abrir();
        var deuda = ConsultarDeudas(con, id: deudaId).FirstOrDefault();
        if (deuda == null) return NotFound();
        if (cuentaPagoId.HasValue && !CuentaEsMia(con, cuentaPagoId.Value)) return Forbid();

        var pagos = ConsultarPagos(con, deudaId);
        CompletarCalculos(deuda, pagos);
        if (capital > deuda.SaldoCapital)
        {
            TempData["Error"] = $"El abono a capital supera el saldo pendiente ({deuda.SaldoCapital:C0}).";
            return RedirectToAction("Detalle", new { id = deudaId });
        }
        if (capital > 0)
            efectoAbono = NormalizarEfectoAbono(efectoAbono);
        else
        {
            efectoAbono = "no_aplica";
            esExtraordinario = false;
        }

        ConversionMoneda conversionTotal;
        try { conversionTotal = Convertir(montoTotal, monedaCodigo, fecha, tasaConversion); }
        catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToAction("Detalle", new { id = deudaId }); }

        var factor = montoTotal == 0 ? 1 : conversionTotal.MontoBase / montoTotal;
        con.Execute(
            @"INSERT INTO deuda_pagos(usuario_id,deuda_id,fecha,monto_total,capital,interes,costos,monto_original,
                         moneda_codigo,tasa_conversion,moneda_base_codigo,cuenta_pago_id,efecto_abono,es_extraordinario,notas)
              VALUES(@UsuarioId,@deudaId,@fecha,@montoBase,@capitalBase,@interesBase,@costosBase,@montoOriginal,
                         @monedaCodigo,@tasa,@monedaBase,@cuentaPagoId,@efectoAbono,@esExtraordinario,@notas)",
            new
            {
                UsuarioId,
                deudaId,
                fecha,
                montoBase = conversionTotal.MontoBase,
                capitalBase = Math.Round(capital * factor, 2),
                interesBase = Math.Round(interes * factor, 2),
                costosBase = Math.Round(costos * factor, 2),
                montoOriginal = conversionTotal.MontoOriginal,
                monedaCodigo = conversionTotal.MonedaOrigen,
                tasa = conversionTotal.Tasa,
                monedaBase = conversionTotal.MonedaDestino,
                cuentaPagoId,
                efectoAbono,
                esExtraordinario,
                notas
            });
        if (deuda.ProximaFechaPago.HasValue)
        {
            con.Execute(
                @"UPDATE deudas SET proxima_fecha_pago = proxima_fecha_pago + INTERVAL '1 month'
                  WHERE id=@deudaId AND usuario_id=@UsuarioId",
                new { deudaId, UsuarioId });
        }
        ActualizarEstado(con, deudaId);
        TempData["Ok"] = "Pago de deuda registrado.";
        return RedirectToAction("Detalle", new { id = deudaId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EliminarPago(int id, int deudaId)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM deuda_pagos WHERE id=@id AND deuda_id=@deudaId AND usuario_id=@UsuarioId", new { id, deudaId, UsuarioId });
        ActualizarEstado(con, deudaId);
        TempData["Ok"] = "Pago eliminado.";
        return RedirectToAction("Detalle", new { id = deudaId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM deudas WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Deuda eliminada con su historial.";
        return RedirectToAction("Index");
    }

    private List<Deuda> ConsultarDeudas(System.Data.IDbConnection con, int? id = null, string? estado = null, string? tipo = null)
    {
        var sql = @"SELECT d.id,d.usuario_id AS UsuarioId,d.acreedor,d.tipo,d.fecha_desembolso AS FechaDesembolso,
                           d.capital_inicial AS CapitalInicial,d.capital_original AS CapitalOriginal,
                           d.moneda_codigo AS MonedaCodigo,d.tasa_conversion AS TasaConversion,d.moneda_base_codigo AS MonedaBaseCodigo,
                           d.tasa,d.periodo_tasa AS PeriodoTasa,d.sistema_pago AS SistemaPago,d.plazo_meses AS PlazoMeses,
                           d.dia_pago AS DiaPago,d.proxima_fecha_pago AS ProximaFechaPago,d.cuota_estimada AS CuotaEstimada,
                           d.cuenta_desembolso_id AS CuentaDesembolsoId,d.cuenta_pago_id AS CuentaPagoId,d.notas,d.estado,
                           cd.nombre AS CuentaDesembolsoNombre, cp.nombre AS CuentaPagoNombre
                    FROM deudas d
                    LEFT JOIN cuentas cd ON cd.id=d.cuenta_desembolso_id
                    LEFT JOIN cuentas cp ON cp.id=d.cuenta_pago_id
                    WHERE d.usuario_id=@UsuarioId";
        if (id.HasValue) sql += " AND d.id=@id";
        if (!string.IsNullOrWhiteSpace(estado)) sql += " AND d.estado=@estado";
        if (!string.IsNullOrWhiteSpace(tipo)) sql += " AND d.tipo=@tipo";
        sql += " ORDER BY d.estado, d.proxima_fecha_pago NULLS LAST, d.fecha_desembolso DESC";
        return con.Query<Deuda>(sql, new { UsuarioId, id, estado, tipo }).ToList();
    }

    private List<DeudaPago> ConsultarPagos(System.Data.IDbConnection con, int? deudaId = null)
    {
        var sql = @"SELECT p.id,p.usuario_id AS UsuarioId,p.deuda_id AS DeudaId,p.fecha,p.monto_total AS MontoTotal,
                           p.capital,p.interes,p.costos,p.monto_original AS MontoOriginal,p.moneda_codigo AS MonedaCodigo,
                           p.tasa_conversion AS TasaConversion,p.moneda_base_codigo AS MonedaBaseCodigo,
                           p.cuenta_pago_id AS CuentaPagoId,c.nombre AS CuentaPagoNombre,
                           COALESCE(p.efecto_abono,'no_aplica') AS EfectoAbono,
                           COALESCE(p.es_extraordinario,FALSE) AS EsExtraordinario,
                           p.notas
                    FROM deuda_pagos p
                    LEFT JOIN cuentas c ON c.id=p.cuenta_pago_id
                    WHERE p.usuario_id=@UsuarioId";
        if (deudaId.HasValue) sql += " AND p.deuda_id=@deudaId";
        sql += " ORDER BY p.fecha DESC, p.id DESC";
        return con.Query<DeudaPago>(sql, new { UsuarioId, deudaId }).ToList();
    }

    private static void CompletarCalculos(Deuda deuda, IEnumerable<DeudaPago> pagos)
    {
        deuda.CapitalPagado = pagos.Sum(x => x.Capital);
        deuda.InteresPagado = pagos.Sum(x => x.Interes);
        deuda.CostosPagados = pagos.Sum(x => x.Costos);
        deuda.TotalPagado = pagos.Sum(x => x.MontoTotal);
    }

    private void ActualizarEstado(System.Data.IDbConnection con, int deudaId)
    {
        con.Execute(
            @"UPDATE deudas d SET estado = CASE
                  WHEN d.capital_inicial <= COALESCE((SELECT SUM(p.capital) FROM deuda_pagos p WHERE p.deuda_id=d.id),0)
                  THEN 'pagada'
                  WHEN d.proxima_fecha_pago IS NOT NULL AND d.proxima_fecha_pago < CURRENT_DATE THEN 'vencida'
                  ELSE 'activa' END
              WHERE d.id=@deudaId AND d.usuario_id=@UsuarioId",
            new { deudaId, UsuarioId });
    }

    private List<Cuenta> CuentasActivas(System.Data.IDbConnection con) =>
        con.Query<Cuenta>("SELECT id,nombre,tipo FROM cuentas WHERE usuario_id=@UsuarioId AND activo ORDER BY tipo,nombre", new { UsuarioId }).ToList();

    private bool CuentaEsMia(System.Data.IDbConnection con, int id) =>
        con.ExecuteScalar<int>("SELECT COUNT(*) FROM cuentas WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId }) > 0;

    private ConversionMoneda Convertir(decimal monto, string monedaCodigo, DateTime fecha, decimal? tasaConversion)
    {
        var pref = _preferencias.Obtener(UsuarioId);
        return _preferencias.ConvertirAsync(monto, monedaCodigo, pref.MonedaCodigo, fecha, tasaConversion)
            .GetAwaiter().GetResult();
    }

    private static string NormalizarTipo(string? tipo, bool permitirVacio = false)
    {
        tipo = (tipo ?? "").Trim().ToLowerInvariant();
        if (permitirVacio && string.IsNullOrWhiteSpace(tipo)) return "";
        return tipo is "banco" or "persona" or "vehiculo" or "hipotecario" or "libranza" or "tarjeta" or "otro"
            ? tipo
            : "personal";
    }

    private static string NormalizarSistema(string? sistema)
    {
        sistema = (sistema ?? "").Trim().ToLowerInvariant();
        return sistema is "cuota_fija" or "solo_intereses" or "abonos_libres" or "sin_interes"
            ? sistema
            : "cuota_fija";
    }

    private static string NormalizarEfectoAbono(string? efecto)
    {
        efecto = (efecto ?? "").Trim().ToLowerInvariant();
        return efecto is "reducir_plazo" or "reducir_cuota" ? efecto : "no_aplica";
    }
}
