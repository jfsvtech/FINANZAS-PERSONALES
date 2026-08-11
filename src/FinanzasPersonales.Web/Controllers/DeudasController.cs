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
    private readonly AuditoriaService _auditoria;

    public DeudasController(Db db, PreferenciasUsuarioService preferencias, AuditoriaService auditoria)
    {
        _db = db;
        _preferencias = preferencias;
        _auditoria = auditoria;
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

    public IActionResult Estrategia(decimal abonoExtraMensual = 0, string metodo = "avalancha")
    {
        metodo = metodo == "bola_nieve" ? "bola_nieve" : "avalancha";
        abonoExtraMensual = Math.Max(0, abonoExtraMensual);
        using var con = _db.Abrir();
        var deudas = ConsultarDeudas(con).Where(x => x.Estado is "activa" or "vencida").ToList();
        var pagos = ConsultarPagos(con).ToLookup(x => x.DeudaId);
        foreach (var deuda in deudas) CompletarCalculos(deuda, pagos[deuda.Id]);
        var items = deudas.Where(x => x.SaldoCapital > 0).Select(x => new EstrategiaDeudaItemVm
        {
            Id = x.Id,
            Acreedor = x.Acreedor,
            Tipo = x.TipoTexto,
            CapitalInicial = x.CapitalInicial,
            CapitalPagado = x.CapitalReconocido,
            InteresPagado = x.InteresPagado,
            SaldoCapital = x.SaldoCapital,
            TasaMensual = x.TasaMensualEquivalente,
            CuotaReferencia = Math.Max(x.CuotaEstimada ?? 0, x.InteresMensualEstimado),
            ProximaFechaPago = x.ProximaFechaPago
        }).ToList();

        var avalancha = items.OrderByDescending(x => x.TasaMensual).ThenByDescending(x => x.SaldoCapital).ToList();
        var bola = items.OrderBy(x => x.SaldoCapital).ThenByDescending(x => x.TasaMensual).ToList();
        for (var i = 0; i < avalancha.Count; i++) avalancha[i].OrdenAvalancha = i + 1;
        for (var i = 0; i < bola.Count; i++) bola[i].OrdenBolaNieve = i + 1;

        var vm = new EstrategiaDeudasVm { Deudas = items, AbonoExtraMensual = abonoExtraMensual, Metodo = metodo };
        vm.Plan = SimularPlan(items, metodo, abonoExtraMensual, out var meses);
        vm.MesesEstimados = meses;
        vm.FechaLibreDeDeudas = meses > 0 ? DateTime.Today.AddMonths(meses) : null;
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string acreedor, string tipo, DateTime fechaDesembolso,
        decimal capitalInicial, decimal tasa, string periodoTasa, string sistemaPago, int plazoMeses,
        int? diaPago, DateTime? proximaFechaPago, decimal? cuotaEstimada, decimal? saldoActualInformado,
        DateTime? fechaSaldoActual, int? cuentaDesembolsoId,
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
        if (saldoActualInformado.HasValue)
        {
            if (saldoActualInformado < 0 || saldoActualInformado > capitalInicial)
            {
                TempData["Error"] = "El saldo actual informado debe estar entre 0 y el capital recibido.";
                return RedirectToAction(id == 0 ? "Index" : "Detalle", id == 0 ? null : new { id });
            }
            fechaSaldoActual ??= DateTime.Today;
        }
        else
        {
            fechaSaldoActual = null;
        }
        ConversionMoneda? conversionSaldoActual = null;
        if (saldoActualInformado.HasValue)
        {
            var fechaSaldo = fechaSaldoActual ?? DateTime.Today;
            try { conversionSaldoActual = Convertir(saldoActualInformado.Value, monedaCodigo, fechaSaldo, tasaConversion); }
            catch (Exception ex) { TempData["Error"] = ex.Message; return RedirectToAction(id == 0 ? "Index" : "Detalle", id == 0 ? null : new { id }); }
        }

        if (id == 0)
        {
            var nuevoId = con.ExecuteScalar<int>(
                @"INSERT INTO deudas(usuario_id,acreedor,tipo,fecha_desembolso,capital_inicial,capital_original,
                         moneda_codigo,tasa_conversion,moneda_base_codigo,tasa,periodo_tasa,sistema_pago,plazo_meses,
                         dia_pago,proxima_fecha_pago,cuota_estimada,saldo_actual_informado,fecha_saldo_actual,
                         cuenta_desembolso_id,cuenta_pago_id,notas)
                  VALUES(@UsuarioId,@acreedor,@tipo,@fechaDesembolso,@capitalBase,@capitalOriginal,@monedaCodigo,
                         @tasaConv,@monedaBase,@tasa,@periodoTasa,@sistemaPago,@plazoMeses,@diaPago,@proximaFechaPago,
                         @cuotaEstimada,@saldoActualBase,@fechaSaldoActual,@cuentaDesembolsoId,@cuentaPagoId,@notas)
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
                    saldoActualBase = conversionSaldoActual?.MontoBase,
                    fechaSaldoActual,
                    cuentaDesembolsoId,
                    cuentaPagoId,
                    notas
                });
            TempData["Ok"] = "Deuda registrada.";
            _auditoria.Registrar(UsuarioId, UsuarioId, "Deudas", "Crear", "deudas", nuevoId, $"Deuda con {acreedor} creada.");
            return RedirectToAction("Detalle", new { id = nuevoId });
        }

        con.Execute(
            @"UPDATE deudas SET acreedor=@acreedor,tipo=@tipo,fecha_desembolso=@fechaDesembolso,
                     capital_inicial=@capitalBase,capital_original=@capitalOriginal,moneda_codigo=@monedaCodigo,
                     tasa_conversion=@tasaConv,moneda_base_codigo=@monedaBase,tasa=@tasa,periodo_tasa=@periodoTasa,
                     sistema_pago=@sistemaPago,plazo_meses=@plazoMeses,dia_pago=@diaPago,
                     proxima_fecha_pago=@proximaFechaPago,cuota_estimada=@cuotaEstimada,
                     saldo_actual_informado=@saldoActualBase,fecha_saldo_actual=@fechaSaldoActual,
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
                saldoActualBase = conversionSaldoActual?.MontoBase,
                fechaSaldoActual,
                cuentaDesembolsoId,
                cuentaPagoId,
                notas
            });
        ActualizarEstado(con, id);
        TempData["Ok"] = "Deuda actualizada.";
        _auditoria.Registrar(UsuarioId, UsuarioId, "Deudas", "Actualizar", "deudas", id, $"Deuda con {acreedor} actualizada.");
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
        _auditoria.Registrar(UsuarioId, UsuarioId, "Deudas", "Registrar pago", "deuda_pagos", deudaId, $"Pago de deuda por {montoTotal:N0} registrado.");
        return RedirectToAction("Detalle", new { id = deudaId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RegistrarCuotaAutomatica(int deudaId, DateTime? fecha = null, string? returnUrl = null)
    {
        using var con = _db.Abrir();
        var deuda = ConsultarDeudas(con, id: deudaId).FirstOrDefault();
        if (deuda == null) return NotFound();
        var resultado = RegistrarCuotaProgramada(con, deuda, fecha?.Date ?? (deuda.ProximaFechaPago?.Date ?? DateTime.Today));
        TempData[resultado.Ok ? "Ok" : "Error"] = resultado.Mensaje;
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction("Detalle", new { id = deudaId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ProcesarCuotasVencidas(string? returnUrl = null)
    {
        using var con = _db.Abrir();
        var deudas = ConsultarDeudas(con).Where(x => (x.Estado is "activa" or "vencida") && x.ProximaFechaPago.HasValue && x.ProximaFechaPago.Value.Date <= DateTime.Today).ToList();
        var ok = 0;
        var errores = new List<string>();
        foreach (var deuda in deudas)
        {
            var resultado = RegistrarCuotaProgramada(con, deuda, deuda.ProximaFechaPago!.Value.Date);
            if (resultado.Ok) ok++;
            else errores.Add($"{deuda.Acreedor}: {resultado.Mensaje}");
        }
        TempData[errores.Any() ? "Error" : "Ok"] = errores.Any()
            ? $"Se registraron {ok} cuota(s). Pendientes: {string.Join(" | ", errores.Take(3))}"
            : $"Se registraron {ok} cuota(s) vencida(s).";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction("Index", "Periodicos");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EliminarPago(int id, int deudaId)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM deuda_pagos WHERE id=@id AND deuda_id=@deudaId AND usuario_id=@UsuarioId", new { id, deudaId, UsuarioId });
        ActualizarEstado(con, deudaId);
        TempData["Ok"] = "Pago eliminado.";
        _auditoria.Registrar(UsuarioId, UsuarioId, "Deudas", "Eliminar pago", "deuda_pagos", id, $"Pago de deuda eliminado.");
        return RedirectToAction("Detalle", new { id = deudaId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM deudas WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Deuda eliminada con su historial.";
        _auditoria.Registrar(UsuarioId, UsuarioId, "Deudas", "Eliminar", "deudas", id, "Deuda eliminada con historial.");
        return RedirectToAction("Index");
    }

    private List<Deuda> ConsultarDeudas(System.Data.IDbConnection con, int? id = null, string? estado = null, string? tipo = null)
    {
        var sql = @"SELECT d.id,d.usuario_id AS UsuarioId,d.acreedor,d.tipo,d.fecha_desembolso AS FechaDesembolso,
                           d.capital_inicial AS CapitalInicial,d.capital_original AS CapitalOriginal,
                           d.moneda_codigo AS MonedaCodigo,d.tasa_conversion AS TasaConversion,d.moneda_base_codigo AS MonedaBaseCodigo,
                           d.tasa,d.periodo_tasa AS PeriodoTasa,d.sistema_pago AS SistemaPago,d.plazo_meses AS PlazoMeses,
                           d.dia_pago AS DiaPago,d.proxima_fecha_pago AS ProximaFechaPago,d.cuota_estimada AS CuotaEstimada,
                           d.saldo_actual_informado AS SaldoActualInformado,d.fecha_saldo_actual AS FechaSaldoActual,
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
        CalculoDeudas.CompletarCalculos(deuda, pagos);
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

    private (bool Ok, string Mensaje) RegistrarCuotaProgramada(System.Data.IDbConnection con, Deuda deuda, DateTime fecha)
    {
        if (deuda.Estado == "pagada") return (false, "La deuda ya esta pagada.");
        var pagos = ConsultarPagos(con, deuda.Id);
        CalculoDeudas.CompletarCalculos(deuda, pagos);
        if (deuda.SaldoCapital <= 0) return (false, "No hay saldo de capital pendiente.");
        if (deuda.CuentaPagoId.HasValue && !CuentaEsMia(con, deuda.CuentaPagoId.Value)) return (false, "La cuenta de pago no es valida.");

        var yaExiste = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM deuda_pagos
              WHERE usuario_id=@UsuarioId AND deuda_id=@deudaId AND fecha=@fecha
                AND notas LIKE 'Cuota programada automatica%'",
            new { UsuarioId, deudaId = deuda.Id, fecha }) > 0;
        if (yaExiste) return (false, "La cuota programada de esa fecha ya fue registrada.");

        var monto = deuda.CuotaEstimada.GetValueOrDefault();
        if (monto <= 0 && deuda.SistemaPago == "sin_interes" && deuda.PlazoMeses > 0)
            monto = Math.Round(deuda.CapitalInicial / deuda.PlazoMeses, 2);
        if (monto <= 0 && deuda.SistemaPago == "solo_intereses")
            monto = deuda.InteresMensualEstimado;
        if (monto <= 0)
            return (false, "Configura cuota estimada o plazo para poder registrar la cuota normal.");

        var interes = deuda.SistemaPago switch
        {
            "sin_interes" => 0,
            "solo_intereses" => Math.Min(monto, deuda.InteresMensualEstimado),
            _ => Math.Min(monto, deuda.InteresMensualEstimado)
        };
        var capital = deuda.SistemaPago == "solo_intereses" ? 0 : Math.Min(deuda.SaldoCapital, Math.Max(0, monto - interes));
        var costos = Math.Max(0, monto - interes - capital);
        if (capital + interes + costos <= 0) return (false, "La cuota no pudo distribuirse entre capital e interes.");

        var pagoId = con.ExecuteScalar<int>(
            @"INSERT INTO deuda_pagos(usuario_id,deuda_id,fecha,monto_total,capital,interes,costos,monto_original,
                       moneda_codigo,tasa_conversion,moneda_base_codigo,cuenta_pago_id,efecto_abono,es_extraordinario,notas)
              VALUES(@UsuarioId,@deudaId,@fecha,@monto,@capital,@interes,@costos,@monto,
                       @monedaCodigo,1,@monedaBase,@cuentaPagoId,'no_aplica',FALSE,@notas)
              RETURNING id",
            new
            {
                UsuarioId,
                deudaId = deuda.Id,
                fecha,
                monto,
                capital,
                interes,
                costos,
                monedaCodigo = deuda.MonedaBaseCodigo,
                monedaBase = deuda.MonedaBaseCodigo,
                deuda.CuentaPagoId,
                notas = $"Cuota programada automatica {fecha:yyyy-MM-dd}"
            });

        if (deuda.CuentaPagoId.HasValue)
        {
            con.Execute(
                @"INSERT INTO movimientos(usuario_id,fecha,tipo,cuenta_id,categoria_id,descripcion,monto,monto_original,moneda_codigo,tasa_conversion,moneda_base_codigo)
                  VALUES(@UsuarioId,@fecha,'gasto',@cuentaId,NULL,@descripcion,@monto,@monto,@moneda,1,@moneda)",
                new
                {
                    UsuarioId,
                    fecha,
                    cuentaId = deuda.CuentaPagoId.Value,
                    descripcion = $"Pago deuda: {deuda.Acreedor}",
                    monto,
                    moneda = deuda.MonedaBaseCodigo
                });
        }

        var siguienteFecha = deuda.ProximaFechaPago.HasValue
            ? deuda.ProximaFechaPago.Value.Date.AddMonths(1)
            : fecha.AddMonths(1);
        con.Execute(
            @"UPDATE deudas SET proxima_fecha_pago=@siguienteFecha
              WHERE id=@deudaId AND usuario_id=@UsuarioId",
            new { siguienteFecha, deudaId = deuda.Id, UsuarioId });
        ActualizarEstado(con, deuda.Id);
        _auditoria.Registrar(UsuarioId, UsuarioId, "Deudas", "Registrar cuota programada", "deuda_pagos", pagoId, $"Cuota automatica de {deuda.Acreedor} por {monto:N0}.");
        return (true, $"Cuota programada de {deuda.Acreedor} registrada por {monto:C0}.");
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

    private static List<EstrategiaPagoPasoVm> SimularPlan(List<EstrategiaDeudaItemVm> origen, string metodo, decimal abonoExtra, out int meses)
    {
        var saldos = origen.Select(x => new EstrategiaDeudaItemVm
        {
            Id = x.Id,
            Acreedor = x.Acreedor,
            Tipo = x.Tipo,
            SaldoCapital = x.SaldoCapital,
            TasaMensual = x.TasaMensual,
            CuotaReferencia = x.CuotaReferencia <= 0 ? Math.Max(1, Math.Round(x.SaldoCapital * 0.03m, 0)) : x.CuotaReferencia
        }).ToList();
        var pasos = new List<EstrategiaPagoPasoVm>();
        meses = 0;
        if (!saldos.Any()) return pasos;
        var pagoBase = saldos.Sum(x => x.CuotaReferencia) + abonoExtra;
        var limite = 600;
        while (saldos.Any(x => x.SaldoCapital > 0) && meses < limite)
        {
            meses++;
            foreach (var deuda in saldos.Where(x => x.SaldoCapital > 0))
                deuda.SaldoCapital += Math.Round(deuda.SaldoCapital * deuda.TasaMensual / 100m, 0);

            var objetivo = metodo == "bola_nieve"
                ? saldos.Where(x => x.SaldoCapital > 0).OrderBy(x => x.SaldoCapital).ThenByDescending(x => x.TasaMensual).First()
                : saldos.Where(x => x.SaldoCapital > 0).OrderByDescending(x => x.TasaMensual).ThenByDescending(x => x.SaldoCapital).First();
            var disponible = pagoBase;
            foreach (var deuda in saldos.Where(x => x.SaldoCapital > 0 && x.Id != objetivo.Id))
            {
                var pago = Math.Min(deuda.SaldoCapital, deuda.CuotaReferencia);
                deuda.SaldoCapital -= pago;
                disponible -= pago;
            }
            objetivo.SaldoCapital -= Math.Min(objetivo.SaldoCapital, Math.Max(0, disponible));
            foreach (var deuda in saldos.Where(x => x.SaldoCapital < 1)) deuda.SaldoCapital = 0;
            if (meses <= 36 || meses % 6 == 0 || saldos.All(x => x.SaldoCapital <= 0))
            {
                pasos.Add(new EstrategiaPagoPasoVm
                {
                    Mes = meses,
                    Objetivo = objetivo.Acreedor,
                    PagoTotal = pagoBase,
                    SaldoRestante = saldos.Sum(x => x.SaldoCapital)
                });
            }
        }
        return pasos;
    }
}
