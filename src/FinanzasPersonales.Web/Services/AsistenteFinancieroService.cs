using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;

namespace FinanzasPersonales.Web.Services;

public class AsistenteFinancieroService
{
    private readonly Db _db;
    private readonly IConfiguration _config;
    private readonly HttpClient _http;

    public AsistenteFinancieroService(Db db, IConfiguration config, HttpClient http)
    {
        _db = db;
        _config = config;
        _http = http;
    }

    public DineroSeguroVm CrearDineroSeguro(int usuarioId, DateTime? fechaReferencia = null)
    {
        var hoy = (fechaReferencia ?? DateTime.Today).Date;
        var desdeMes = new DateTime(hoy.Year, hoy.Month, 1);
        var hastaMes = desdeMes.AddMonths(1);
        using var con = _db.Abrir();
        var p = new { usuarioId, hoy, desdeMes, hastaMes };

        var liquidez = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(
                    CASE WHEN m.tipo='ingreso' AND co.tipo<>'tarjeta_credito' THEN m.monto ELSE 0 END
                  - CASE WHEN m.tipo='gasto' AND co.tipo<>'tarjeta_credito' THEN m.monto ELSE 0 END
                  - CASE WHEN m.tipo='pago_tarjeta' AND co.tipo<>'tarjeta_credito' THEN m.monto ELSE 0 END
                  - CASE WHEN m.tipo='transferencia' AND co.tipo<>'tarjeta_credito' THEN m.monto ELSE 0 END
                  + CASE WHEN m.tipo='transferencia' AND cd.tipo<>'tarjeta_credito' THEN m.monto ELSE 0 END)
              FROM movimientos m
              JOIN cuentas co ON co.id=m.cuenta_id
              LEFT JOIN cuentas cd ON cd.id=m.cuenta_destino_id
              WHERE m.usuario_id=@usuarioId",
            new { usuarioId }) ?? 0;

        var ingresosPendientes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto_estimado) FROM gastos_periodicos
              WHERE usuario_id=@usuarioId AND activo AND tipo='ingreso'
                AND proxima_fecha>=@hoy AND proxima_fecha<@hastaMes", p) ?? 0;
        var gastosPendientes = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(monto_estimado) FROM gastos_periodicos
              WHERE usuario_id=@usuarioId AND activo AND tipo='gasto'
                AND proxima_fecha>=@hoy AND proxima_fecha<@hastaMes", p) ?? 0;
        var cuotasDeuda = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(COALESCE(cuota_estimada,0)) FROM deudas
              WHERE usuario_id=@usuarioId AND estado IN ('activa','vencida')
                AND proxima_fecha_pago>=@hoy AND proxima_fecha_pago<@hastaMes", p) ?? 0;
        var pagosTarjeta = con.ExecuteScalar<decimal?>(
            @"WITH deuda_tarjetas AS (
                  SELECT c.id,c.dia_pago,
                         COALESCE((SELECT SUM(CASE
                              WHEN m.tipo='gasto' AND m.cuenta_id=c.id THEN m.monto
                              WHEN m.tipo='pago_tarjeta' AND m.cuenta_destino_id=c.id THEN -m.monto
                              ELSE 0 END)
                         FROM movimientos m
                         WHERE m.usuario_id=@usuarioId AND (m.cuenta_id=c.id OR m.cuenta_destino_id=c.id)),0) deuda
                  FROM cuentas c
                  WHERE c.usuario_id=@usuarioId AND c.activo AND c.tipo='tarjeta_credito'
              )
              SELECT SUM(GREATEST(0,deuda)) FROM deuda_tarjetas
              WHERE dia_pago IS NULL OR make_date(EXTRACT(YEAR FROM @hoy::date)::int, EXTRACT(MONTH FROM @hoy::date)::int, LEAST(dia_pago,28)) BETWEEN @hoy AND (@hastaMes::date - INTERVAL '1 day')",
            p) ?? 0;

        var metasComprometidas = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                    WHEN fecha_objetivo IS NULL THEN 0
                    ELSE GREATEST(0, monto_objetivo - COALESCE((SELECT SUM(a.monto) FROM aportes_meta a WHERE a.meta_ahorro_id=m.id),0))
                         / GREATEST(1, ((EXTRACT(YEAR FROM fecha_objetivo)::int - EXTRACT(YEAR FROM @hoy::date)::int) * 12)
                                      + EXTRACT(MONTH FROM fecha_objetivo)::int - EXTRACT(MONTH FROM @hoy::date)::int)
                  END)
              FROM metas_ahorro m
              WHERE m.usuario_id=@usuarioId AND m.activo AND fecha_objetivo IS NOT NULL AND fecha_objetivo>=@hoy", p) ?? 0;
        var colchon = con.ExecuteScalar<decimal?>(
            "SELECT colchon_seguridad FROM configuraciones_usuario WHERE usuario_id=@usuarioId",
            new { usuarioId }) ?? 0;

        return new DineroSeguroVm
        {
            LiquidezActual = liquidez,
            IngresosRecurrentesPendientes = ingresosPendientes,
            GastosRecurrentesPendientes = gastosPendientes,
            PagosTarjetaEstimados = Math.Max(0, pagosTarjeta),
            CuotasDeudaPendientes = cuotasDeuda,
            MetasComprometidas = metasComprometidas,
            ColchonSeguridad = colchon,
            DiasRestantesMes = Math.Max(1, (hastaMes - hoy).Days)
        };
    }

    public SaludFinancieraVm CalcularSaludFinanciera(int usuarioId)
    {
        var hoy = DateTime.Today;
        var desde = new DateTime(hoy.Year, hoy.Month, 1);
        var hasta = desde.AddMonths(1);
        using var con = _db.Abrir();
        var p = new { usuarioId, desde, hasta };
        var ingresos = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        var salidas = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                    WHEN m.tipo='gasto' AND c.tipo<>'tarjeta_credito' THEN m.monto
                    WHEN m.tipo='pago_tarjeta' THEN m.monto
                    ELSE 0 END)
              FROM movimientos m JOIN cuentas c ON c.id=m.cuenta_id
              WHERE m.usuario_id=@usuarioId AND m.fecha>=@desde AND m.fecha<@hasta
                AND m.tipo IN ('gasto','pago_tarjeta')", p) ?? 0;
        var liquidez = CrearDineroSeguro(usuarioId).LiquidezActual;
        var deudaTotal = con.ExecuteScalar<decimal?>(
            @"SELECT COALESCE((SELECT SUM(GREATEST(0,d.capital_inicial - COALESCE((SELECT SUM(dp.capital) FROM deuda_pagos dp WHERE dp.deuda_id=d.id),0)))
                    FROM deudas d WHERE d.usuario_id=@usuarioId AND d.estado IN ('activa','vencida')),0)
                + COALESCE((SELECT SUM(GREATEST(0,deuda)) FROM (
                    SELECT COALESCE((SELECT SUM(CASE
                         WHEN m.tipo='gasto' AND m.cuenta_id=c.id THEN m.monto
                         WHEN m.tipo='pago_tarjeta' AND m.cuenta_destino_id=c.id THEN -m.monto
                         ELSE 0 END)
                    FROM movimientos m WHERE m.usuario_id=@usuarioId AND (m.cuenta_id=c.id OR m.cuenta_destino_id=c.id)),0) deuda
                    FROM cuentas c WHERE c.usuario_id=@usuarioId AND c.tipo='tarjeta_credito') t),0)",
            new { usuarioId }) ?? 0;
        var vencidos = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM deudas WHERE usuario_id=@usuarioId AND estado IN ('activa','vencida')
              AND proxima_fecha_pago IS NOT NULL AND proxima_fecha_pago<CURRENT_DATE", new { usuarioId });

        var tasaAhorro = ingresos > 0 ? Math.Round((ingresos - salidas) * 100 / ingresos, 1) : 0;
        var gastoPromedioDia = salidas / Math.Max(1, DateTime.DaysInMonth(hoy.Year, hoy.Month));
        var cobertura = gastoPromedioDia > 0 ? Math.Round(liquidez / gastoPromedioDia, 1) : liquidez > 0 ? 999 : 0;
        var relacionDeuda = liquidez > 0 ? Math.Round(deudaTotal * 100 / liquidez, 1) : deudaTotal > 0 ? 999 : 0;

        var puntaje = 50;
        puntaje += tasaAhorro >= 20 ? 20 : tasaAhorro >= 10 ? 12 : tasaAhorro >= 0 ? 4 : -15;
        puntaje += cobertura >= 30 ? 20 : cobertura >= 15 ? 12 : cobertura >= 7 ? 6 : -10;
        puntaje += relacionDeuda <= 50 ? 15 : relacionDeuda <= 150 ? 5 : -15;
        puntaje -= Math.Min(20, vencidos * 8);
        puntaje = Math.Clamp(puntaje, 0, 100);

        var vm = new SaludFinancieraVm
        {
            Puntaje = puntaje,
            TasaAhorro = tasaAhorro,
            CoberturaLiquidez = cobertura,
            RelacionDeudaLiquidez = relacionDeuda,
            AlertasCriticas = vencidos,
            Estado = puntaje >= 80 ? "Excelente" : puntaje >= 65 ? "Saludable" : puntaje >= 45 ? "En observacion" : "Critica",
            Color = puntaje >= 80 ? "success" : puntaje >= 65 ? "primary" : puntaje >= 45 ? "warning" : "danger"
        };
        vm.Factores.Add($"Ahorro del periodo: {tasaAhorro:0.#}%");
        vm.Factores.Add($"Cobertura de liquidez: {cobertura:0.#} dias");
        vm.Factores.Add($"Deuda sobre liquidez: {relacionDeuda:0.#}%");
        if (vencidos > 0) vm.Factores.Add($"{vencidos} pago(s) vencido(s)");
        return vm;
    }

    public List<RadarFinancieroItemVm> CrearRadar(int usuarioId)
    {
        var hoy = DateTime.Today;
        var desde = new DateTime(hoy.Year, hoy.Month, 1);
        var hasta = desde.AddMonths(1);
        var anterior = desde.AddMonths(-1);
        using var con = _db.Abrir();
        var radar = new List<RadarFinancieroItemVm>();
        var dineroSeguro = CrearDineroSeguro(usuarioId);

        if (dineroSeguro.Valor < 0)
            radar.Add(new() { Severidad = "danger", Icono = "bi-shield-exclamation", Titulo = "Dinero seguro negativo", Detalle = $"Faltan {Math.Abs(dineroSeguro.Valor):C0} para cubrir compromisos del mes.", Controller = "Dashboard", Accion = "Revisar caja" });
        else if (dineroSeguro.PromedioDiario > 0)
            radar.Add(new() { Severidad = "success", Icono = "bi-shield-check", Titulo = "Caja controlada", Detalle = $"Puedes gastar cerca de {dineroSeguro.PromedioDiario:C0} diarios sin afectar compromisos.", Controller = "Movimientos", Accion = "Ver movimientos" });

        var categoriaSpike = con.QueryFirstOrDefault<(string Nombre, string Icono, decimal Actual, decimal Anterior)>(
            @"SELECT c.nombre,c.icono,
                     COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@desde AND m.fecha<@hasta),0) AS Actual,
                     COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@anterior AND m.fecha<@desde),0) AS Anterior
              FROM categorias c
              LEFT JOIN movimientos m ON m.categoria_id=c.id AND m.usuario_id=@usuarioId AND m.tipo='gasto'
                   AND m.fecha>=@anterior AND m.fecha<@hasta
              WHERE c.usuario_id=@usuarioId AND c.tipo='gasto'
              GROUP BY c.id,c.nombre,c.icono
              HAVING COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@desde AND m.fecha<@hasta),0) >
                     GREATEST(50000, COALESCE(SUM(m.monto) FILTER (WHERE m.fecha>=@anterior AND m.fecha<@desde),0) * 1.35)
              ORDER BY Actual - Anterior DESC LIMIT 1",
            new { usuarioId, desde, hasta, anterior });
        if (!string.IsNullOrWhiteSpace(categoriaSpike.Nombre))
            radar.Add(new() { Severidad = "warning", Icono = categoriaSpike.Icono, Titulo = $"Subida inusual en {categoriaSpike.Nombre}", Detalle = $"Va en {categoriaSpike.Actual:C0}; el periodo anterior fue {categoriaSpike.Anterior:C0}.", Controller = "Movimientos", Accion = "Filtrar categoria" });

        var presupuestosRiesgo = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM presupuestos pr WHERE pr.usuario_id=@usuarioId AND
              COALESCE((SELECT SUM(m.monto) FROM movimientos m WHERE m.usuario_id=@usuarioId AND m.tipo='gasto'
              AND m.categoria_id=pr.categoria_id AND m.fecha>=@desde AND m.fecha<@hasta),0)>=pr.monto_mensual*.9", new { usuarioId, desde, hasta });
        if (presupuestosRiesgo > 0)
            radar.Add(new() { Severidad = "warning", Icono = "bi-bullseye", Titulo = "Presupuestos cerca del limite", Detalle = $"{presupuestosRiesgo} presupuesto(s) estan al 90% o mas.", Controller = "Presupuestos", Accion = "Ver presupuestos" });

        var recurrenteDetectado = con.QueryFirstOrDefault<(string Descripcion, decimal Promedio, int Veces)>(
            @"SELECT clave AS Descripcion, AVG(monto) AS Promedio, COUNT(*)::int AS Veces
              FROM (
                  SELECT LOWER(LEFT(REGEXP_REPLACE(COALESCE(descripcion,''), '\s+', ' ', 'g'), 36)) AS clave, monto
                  FROM movimientos
                  WHERE usuario_id=@usuarioId AND tipo='gasto' AND gasto_periodico_id IS NULL
                    AND fecha>=CURRENT_DATE-120 AND COALESCE(descripcion,'')<>''
              ) x
              GROUP BY clave
              HAVING COUNT(*)>=2 AND MAX(monto)-MIN(monto)<=GREATEST(5000, AVG(monto)*0.15)
              ORDER BY COUNT(*) DESC, AVG(monto) DESC
              LIMIT 1", new { usuarioId });
        if (!string.IsNullOrWhiteSpace(recurrenteDetectado.Descripcion))
            radar.Add(new() { Severidad = "info", Icono = "bi-arrow-repeat", Titulo = "Posible gasto recurrente detectado", Detalle = $"{recurrenteDetectado.Descripcion} aparece {recurrenteDetectado.Veces} veces por cerca de {recurrenteDetectado.Promedio:C0}.", Controller = "Periodicos", Accion = "Crear recurrente" });

        var vencimientos = con.ExecuteScalar<int>(
            @"SELECT
                COALESCE((SELECT COUNT(*) FROM gastos_periodicos WHERE usuario_id=@usuarioId AND activo AND proxima_fecha<CURRENT_DATE),0)
                + COALESCE((SELECT COUNT(*) FROM deudas WHERE usuario_id=@usuarioId AND estado IN ('activa','vencida') AND proxima_fecha_pago<CURRENT_DATE),0)",
            new { usuarioId });
        if (vencimientos > 0)
            radar.Add(new() { Severidad = "danger", Icono = "bi-calendar-x", Titulo = "Vencimientos pendientes", Detalle = $"Hay {vencimientos} compromiso(s) vencido(s).", Controller = "Calendario", Accion = "Abrir calendario" });

        var inversionesSinValorar = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM inversiones i WHERE i.usuario_id=@usuarioId AND i.estado='activa'
              AND i.tipo_rendimiento='variable' AND COALESCE(
              (SELECT MAX(v.fecha) FROM inversion_valoraciones v WHERE v.inversion_id=i.id),i.fecha_inicio)<CURRENT_DATE-45",
            new { usuarioId });
        if (inversionesSinValorar > 0)
            radar.Add(new() { Severidad = "info", Icono = "bi-speedometer2", Titulo = "Portafolio sin valorar", Detalle = $"{inversionesSinValorar} inversion(es) variables necesitan valor actualizado.", Controller = "Inversiones", Action = "Tablero", Accion = "Actualizar" });

        return radar.Take(6).ToList();
    }

    public string ResponderPregunta(int usuarioId, string pregunta)
    {
        pregunta = (pregunta ?? "").Trim();
        if (string.IsNullOrWhiteSpace(pregunta))
            return "Escribe una pregunta como: cuanto puedo gastar hoy, cual deuda priorizo, donde se fue mi dinero o como esta mi salud financiera.";

        var normal = Normalizar(pregunta);
        var dinero = CrearDineroSeguro(usuarioId);
        var salud = CalcularSaludFinanciera(usuarioId);
        var radar = CrearRadar(usuarioId);
        var recomendaciones = CrearRecomendaciones(usuarioId);
        using var con = _db.Abrir();
        var hoy = DateTime.Today;
        var desde = new DateTime(hoy.Year, hoy.Month, 1);
        var hasta = desde.AddMonths(1);

        if (normal.Contains("gastar") || normal.Contains("disponible") || normal.Contains("seguro"))
            return $"Puedes tomar como referencia {dinero.Valor:C0} de dinero seguro para gastar. Eso equivale a {dinero.PromedioDiario:C0} por dia hasta cerrar el mes, despues de descontar recurrentes, tarjetas, deudas, metas y colchon.";

        if (normal.Contains("deuda") || normal.Contains("pagar primero") || normal.Contains("avalancha"))
        {
            var deuda = con.QueryFirstOrDefault<Deuda>(
                @"SELECT acreedor,capital_inicial AS CapitalInicial,tasa,periodo_tasa AS PeriodoTasa,
                         COALESCE((SELECT SUM(dp.capital) FROM deuda_pagos dp WHERE dp.deuda_id=d.id),0) AS CapitalPagado
                  FROM deudas d
                  WHERE d.usuario_id=@usuarioId AND d.estado IN ('activa','vencida')
                  ORDER BY CASE WHEN d.periodo_tasa='anual' THEN (POWER(1+d.tasa/100.0,1.0/12.0)-1)*100 ELSE d.tasa END DESC
                  LIMIT 1", new { usuarioId });
            return deuda == null
                ? "No tienes deudas activas registradas. Si vas a tomar una deuda nueva, registrala para simular cuota, intereses y estrategia."
                : $"Prioriza {deuda.Acreedor}: saldo aproximado {deuda.SaldoCapital:C0} y tasa mensual equivalente {deuda.TasaMensualEquivalente:0.##}%. Es la candidata natural para metodo avalancha.";
        }

        if (normal.Contains("gasto") || normal.Contains("dinero"))
        {
            var top = con.QueryFirstOrDefault<GastoCategoriaVm>(
                @"SELECT c.nombre,c.icono,c.color,SUM(m.monto) total
                  FROM movimientos m JOIN categorias c ON c.id=m.categoria_id
                  WHERE m.usuario_id=@usuarioId AND m.tipo='gasto' AND m.fecha>=@desde AND m.fecha<@hasta
                  GROUP BY c.id,c.nombre,c.icono,c.color ORDER BY total DESC LIMIT 1",
                new { usuarioId, desde, hasta });
            return top == null
                ? "Aun no hay gastos suficientes este mes para detectar una categoria dominante."
                : $"La categoria que mas pesa este mes es {top.Nombre}, con {top.Total:C0}. Si quieres ajustar rapido, empieza revisando esos movimientos.";
        }

        if (normal.Contains("salud") || normal.Contains("riesgo"))
            return $"Tu salud financiera esta en {salud.Estado} con {salud.Puntaje}/100. Factores principales: {string.Join("; ", salud.Factores)}.";

        if (normal.Contains("inversion"))
            return recomendaciones.FirstOrDefault(x => x.Controller == "Inversiones")?.Detalle
                ?? "Tus inversiones no muestran alertas criticas. Mantener valoraciones actualizadas hace que patrimonio y rentabilidad sean mas confiables.";

        var radarPrincipal = radar.FirstOrDefault();
        if (radarPrincipal != null)
            return $"Lo mas importante ahora: {radarPrincipal.Titulo}. {radarPrincipal.Detalle}";
        var recomendacionPrincipal = recomendaciones.FirstOrDefault();
        return recomendacionPrincipal == null
            ? $"No veo alertas fuertes ahora. Dinero seguro: {dinero.Valor:C0}; salud financiera: {salud.Puntaje}/100."
            : $"Lo mas importante ahora: {recomendacionPrincipal.Titulo}. {recomendacionPrincipal.Detalle}";
    }

    public InformeMensualVm CrearInforme(int usuarioId, int anio, int mes)
    {
        var desde = new DateTime(anio, mes, 1);
        var hasta = desde.AddMonths(1);
        var anterior = desde.AddMonths(-1);
        using var con = _db.Abrir();
        var p = new { usuarioId, desde, hasta, anterior };
        var vm = new InformeMensualVm { Anio = anio, Mes = mes };
        vm.Ingresos = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        vm.Gastos = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        vm.IngresosAnterior = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@anterior AND fecha<@desde", p) ?? 0;
        vm.GastosAnterior = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@anterior AND fecha<@desde", p) ?? 0;
        vm.DeudaTarjetas = Math.Max(0, con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE WHEN m.tipo='gasto' THEN m.monto WHEN m.tipo='pago_tarjeta' THEN -m.monto ELSE 0 END)
              FROM movimientos m JOIN cuentas c ON c.id=COALESCE(m.cuenta_destino_id,m.cuenta_id)
              WHERE m.usuario_id=@usuarioId AND c.tipo='tarjeta_credito'
                AND ((m.tipo='gasto' AND m.cuenta_id=c.id) OR (m.tipo='pago_tarjeta' AND m.cuenta_destino_id=c.id))",
            new { usuarioId }) ?? 0);
        vm.SaldoPorCobrar = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(GREATEST(0,p.capital - COALESCE((SELECT SUM(pp.monto) FROM prestamo_pagos pp
              WHERE pp.prestamo_id=p.id AND pp.tipo='abono_capital'),0)))
              FROM prestamos p WHERE p.usuario_id=@usuarioId AND p.estado='activo'", new { usuarioId }) ?? 0;
        vm.SaldoPorPagar = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(GREATEST(0,d.capital_inicial - COALESCE((SELECT SUM(dp.capital) FROM deuda_pagos dp
              WHERE dp.deuda_id=d.id),0)))
              FROM deudas d WHERE d.usuario_id=@usuarioId AND d.estado IN ('activa','vencida')", new { usuarioId }) ?? 0;
        vm.ValorInversiones = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(GREATEST(0,COALESCE(v.valor,i.capital_inicial) +
              COALESCE((SELECT SUM(CASE WHEN m.tipo IN ('aporte','rendimiento') THEN m.monto ELSE -m.monto END)
              FROM inversion_movimientos m WHERE m.inversion_id=i.id AND (v.fecha IS NULL OR m.fecha>v.fecha)),0)))
              FROM inversiones i LEFT JOIN LATERAL (
              SELECT valor,fecha FROM inversion_valoraciones WHERE inversion_id=i.id ORDER BY fecha DESC,id DESC LIMIT 1
              ) v ON TRUE WHERE i.usuario_id=@usuarioId AND i.estado='activa'", new { usuarioId }) ?? 0;
        vm.Categorias = con.Query<GastoCategoriaVm>(
            @"SELECT c.nombre,c.color,c.icono,SUM(m.monto) total FROM movimientos m
              JOIN categorias c ON c.id=m.categoria_id
              WHERE m.usuario_id=@usuarioId AND m.tipo='gasto' AND m.fecha>=@desde AND m.fecha<@hasta
              GROUP BY c.id,c.nombre,c.color,c.icono ORDER BY total DESC", p).ToList();
        vm.Recomendaciones = CrearRecomendaciones(usuarioId, desde, hasta);
        return vm;
    }

    public List<RecomendacionFinancieraVm> CrearRecomendaciones(int usuarioId, DateTime? desdeMes = null, DateTime? hastaMes = null)
    {
        var desde = desdeMes ?? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var hasta = hastaMes ?? desde.AddMonths(1);
        var anterior = desde.AddMonths(-1);
        using var con = _db.Abrir();
        var p = new { usuarioId, desde, hasta, anterior };
        var ingresos = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='ingreso' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        var gastos = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@desde AND fecha<@hasta", p) ?? 0;
        var gastosAnterior = con.ExecuteScalar<decimal?>("SELECT SUM(monto) FROM movimientos WHERE usuario_id=@usuarioId AND tipo='gasto' AND fecha>=@anterior AND fecha<@desde", p) ?? 0;
        var liquidez = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                    WHEN m.tipo='ingreso' AND c.tipo<>'tarjeta_credito' THEN m.monto
                    WHEN m.tipo='gasto' AND c.tipo<>'tarjeta_credito' THEN -m.monto
                    WHEN m.tipo='pago_tarjeta' THEN -m.monto
                    ELSE 0 END)
              FROM movimientos m JOIN cuentas c ON c.id=m.cuenta_id
              WHERE m.usuario_id=@usuarioId", new { usuarioId }) ?? 0;
        var deudaTarjetas = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(CASE
                  WHEN m.tipo='gasto' THEN m.monto
                  WHEN m.tipo='pago_tarjeta' THEN -m.monto
                  ELSE 0 END)
              FROM movimientos m
              JOIN cuentas c ON c.id = COALESCE(m.cuenta_destino_id, m.cuenta_id)
              WHERE m.usuario_id=@usuarioId AND c.tipo='tarjeta_credito'
                AND ((m.tipo='gasto' AND m.cuenta_id=c.id) OR (m.tipo='pago_tarjeta' AND m.cuenta_destino_id=c.id))",
            new { usuarioId }) ?? 0;
        deudaTarjetas = Math.Max(0, deudaTarjetas);
        var deudaPrincipal = con.QueryFirstOrDefault<Deuda>(
            @"SELECT d.id,d.acreedor,d.tipo,d.capital_inicial AS CapitalInicial,d.tasa,d.periodo_tasa AS PeriodoTasa,
                     COALESCE((SELECT SUM(dp.capital) FROM deuda_pagos dp WHERE dp.deuda_id=d.id),0) AS CapitalPagado
              FROM deudas d
              WHERE d.usuario_id=@usuarioId AND d.estado IN ('activa','vencida')
              ORDER BY
                (CASE WHEN d.periodo_tasa='anual'
                    THEN (POWER(1 + d.tasa / 100.0, 1.0 / 12.0) - 1) * 100
                    ELSE d.tasa END) DESC,
                d.capital_inicial DESC
              LIMIT 1", new { usuarioId });
        var saldoDeudas = con.ExecuteScalar<decimal?>(
            @"SELECT SUM(GREATEST(0,d.capital_inicial - COALESCE((
                  SELECT SUM(dp.capital) FROM deuda_pagos dp WHERE dp.deuda_id=d.id
              ),0)))
              FROM deudas d WHERE d.usuario_id=@usuarioId AND d.estado IN ('activa','vencida')",
            new { usuarioId }) ?? 0;
        var proximaDeuda = con.QueryFirstOrDefault<Deuda>(
            @"SELECT id,acreedor,proxima_fecha_pago AS ProximaFechaPago,cuota_estimada AS CuotaEstimada
              FROM deudas
              WHERE usuario_id=@usuarioId AND estado IN ('activa','vencida') AND proxima_fecha_pago IS NOT NULL
              ORDER BY proxima_fecha_pago LIMIT 1", new { usuarioId });
        var recomendaciones = new List<RecomendacionFinancieraVm>();

        if (ingresos > 0 && gastos > ingresos)
            recomendaciones.Add(new() { Tipo="danger", Icono="bi-exclamation-triangle", Titulo="Estas gastando mas de lo que recibes", Detalle=$"El deficit del periodo es {(gastos-ingresos):C0}.", Accion="Revisar movimientos", Controller="Movimientos" });
        else if (ingresos > 0 && (ingresos - gastos) / ingresos < .10m)
            recomendaciones.Add(new() { Tipo="warning", Icono="bi-piggy-bank", Titulo="Tu margen de ahorro es menor al 10%", Detalle="Un pequeño ajuste en gastos variables puede fortalecer tu liquidez.", Accion="Revisar presupuestos", Controller="Presupuestos" });
        else if (ingresos > 0)
            recomendaciones.Add(new() { Tipo="success", Icono="bi-graph-up-arrow", Titulo="Buen resultado mensual", Detalle=$"Has conservado {(ingresos-gastos):C0} durante el periodo.", Accion="Aportar a una meta", Controller="Metas" });

        if (gastosAnterior > 0 && gastos > gastosAnterior * 1.20m)
            recomendaciones.Add(new() { Tipo="warning", Icono="bi-arrow-up-right", Titulo="Tus gastos aumentaron mas de 20%", Detalle=$"Gastaste {(gastos-gastosAnterior):C0} mas que el mes anterior.", Accion="Ver comparacion", Controller="Dashboard" });

        if (saldoDeudas > 0 && deudaPrincipal != null)
            recomendaciones.Add(new()
            {
                Tipo = "danger",
                Icono = "bi-shield-exclamation",
                Titulo = $"Prioriza la deuda con mayor costo: {deudaPrincipal.Acreedor}",
                Detalle = $"Tiene una tasa mensual equivalente de {deudaPrincipal.TasaMensualEquivalente:0.##}% y saldo aproximado de {deudaPrincipal.SaldoCapital:C0}. Un abono extra ahi suele ahorrar mas intereses.",
                Accion = "Analizar deudas",
                Controller = "Prestamos",
                Action = "Tablero"
            });

        if (proximaDeuda?.ProximaFechaPago.HasValue == true && proximaDeuda.ProximaFechaPago.Value.Date <= DateTime.Today.AddDays(7))
            recomendaciones.Add(new()
            {
                Tipo = proximaDeuda.ProximaFechaPago.Value.Date < DateTime.Today ? "danger" : "warning",
                Icono = "bi-calendar2-week",
                Titulo = $"Pago cercano: {proximaDeuda.Acreedor}",
                Detalle = $"Vence el {proximaDeuda.ProximaFechaPago:dd MMM yyyy}. Monto de referencia: {(proximaDeuda.CuotaEstimada ?? 0):C0}.",
                Accion = "Ver calendario",
                Controller = "Calendario"
            });

        if (liquidez > 0 && saldoDeudas + deudaTarjetas > liquidez * 2)
            recomendaciones.Add(new()
            {
                Tipo = "warning",
                Icono = "bi-bank",
                Titulo = "Tu deuda supera dos veces tu liquidez",
                Detalle = $"Pasivos estimados: {(saldoDeudas + deudaTarjetas):C0}. Liquidez estimada: {liquidez:C0}. Conviene preparar un plan de abonos.",
                Accion = "Ver analisis crediticio",
                Controller = "Prestamos",
                Action = "Tablero"
            });

        if (deudaTarjetas > 0)
            recomendaciones.Add(new()
            {
                Tipo = "info",
                Icono = "bi-credit-card",
                Titulo = "Controla el uso de tarjetas de credito",
                Detalle = $"Tu deuda actual en tarjetas es {deudaTarjetas:C0}. El pago de tarjeta impacta la caja cuando realmente sale el dinero.",
                Accion = "Ver movimientos",
                Controller = "Movimientos"
            });

        var categoria = con.QueryFirstOrDefault<GastoCategoriaVm>(
            @"SELECT c.nombre,c.color,c.icono,SUM(m.monto) total FROM movimientos m JOIN categorias c ON c.id=m.categoria_id
              WHERE m.usuario_id=@usuarioId AND m.tipo='gasto' AND m.fecha>=@desde AND m.fecha<@hasta
              GROUP BY c.id,c.nombre,c.color,c.icono ORDER BY total DESC LIMIT 1", p);
        if (categoria != null && gastos > 0 && categoria.Total / gastos >= .35m)
            recomendaciones.Add(new() { Tipo="info", Icono=categoria.Icono, Titulo=$"{categoria.Nombre} concentra gran parte del gasto", Detalle=$"Representa {Math.Round(categoria.Total*100/gastos)}% del total mensual.", Accion="Revisar categoria", Controller="Movimientos" });

        var presupuestosRiesgo = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM presupuestos pr WHERE pr.usuario_id=@usuarioId AND
              COALESCE((SELECT SUM(m.monto) FROM movimientos m WHERE m.usuario_id=@usuarioId AND m.tipo='gasto'
              AND m.categoria_id=pr.categoria_id AND m.fecha>=@desde AND m.fecha<@hasta),0)>=pr.monto_mensual*.8", p);
        if (presupuestosRiesgo > 0)
            recomendaciones.Add(new() { Tipo="warning", Icono="bi-bullseye", Titulo=$"{presupuestosRiesgo} presupuesto(s) requieren atencion", Detalle="Ya alcanzaron al menos el 80% del limite definido.", Accion="Ver presupuestos", Controller="Presupuestos" });
        var inversionesSinValorar = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM inversiones i WHERE i.usuario_id=@usuarioId AND i.estado='activa'
              AND i.tipo_rendimiento='variable' AND COALESCE(
              (SELECT MAX(v.fecha) FROM inversion_valoraciones v WHERE v.inversion_id=i.id),i.fecha_inicio)<CURRENT_DATE-45",
            new { usuarioId });
        if (inversionesSinValorar > 0)
            recomendaciones.Add(new() { Tipo="info", Icono="bi-speedometer", Titulo=$"{inversionesSinValorar} inversion(es) necesitan valoracion", Detalle="Actualiza su valor de mercado para que patrimonio y rentabilidad sean confiables.", Accion="Ver portafolio", Controller="Inversiones" });
        return recomendaciones;
    }

    public RegistroNaturalVm Interpretar(int usuarioId, string texto)
    {
        using var con = _db.Abrir();
        var vm = CrearVmBase(con, usuarioId);
        vm.Texto = texto?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(vm.Texto)) return vm;

        InterpretarPorReglas(vm, vm.Texto);
        vm.Interpretado = vm.Monto > 0;
        return vm;
    }

    public async Task<RegistroNaturalVm> InterpretarDocumentoAsync(int usuarioId, string textoOcr, string? nombreArchivo)
    {
        using var con = _db.Abrir();
        var vm = CrearVmBase(con, usuarioId);
        vm.Modo = "documento";
        vm.TextoOcr = (textoOcr ?? "").Trim();
        vm.Texto = vm.TextoOcr;
        vm.NombreArchivo = nombreArchivo;
        vm.ProveedorOcr = "tesseract-js";

        if (string.IsNullOrWhiteSpace(vm.TextoOcr))
        {
            vm.MensajeDocumento = "No recibi texto OCR suficiente. Intenta una foto mas nitida o escribe el texto detectado.";
            return vm;
        }

        var interpretadoPorIa = await IntentarInterpretarConIaAsync(vm);
        if (!interpretadoPorIa)
        {
            InterpretarPorReglas(vm, vm.TextoOcr);
            vm.ProveedorIa = "reglas";
            vm.Confianza = CalcularConfianza(vm);
        }

        vm.Interpretado = vm.Monto > 0;
        if (vm.Monto <= 0) vm.AlertasDocumento.Add("No pude identificar un valor total confiable.");
        if (!vm.CategoriaId.HasValue) vm.AlertasDocumento.Add("Selecciona la categoria antes de guardar.");
        if (!vm.CuentaId.HasValue) vm.AlertasDocumento.Add("Selecciona la cuenta o medio de pago.");
        vm.MensajeDocumento = vm.Interpretado
            ? "Documento leido. Revisa la propuesta antes de guardar."
            : "Leimos el documento, pero faltan datos para registrar el movimiento.";
        return vm;
    }

    public int RegistrarAuditoriaDocumento(int usuarioId, string textoOcr, string? nombreArchivo, string? contentType,
        long tamanoBytes, bool imagenGuardada, string? rutaArchivo, string proveedorOcr, string proveedorIa,
        decimal confianza, string? respuestaIaJson)
    {
        using var con = _db.Abrir();
        return con.ExecuteScalar<int>(
            @"INSERT INTO documentos_movimiento_ocr(usuario_id,nombre_archivo,content_type,tamano_bytes,texto_extraido,
                     proveedor_ocr,proveedor_ia,respuesta_ia_json,confianza,imagen_guardada,ruta_archivo)
              VALUES(@usuarioId,@nombreArchivo,@contentType,@tamanoBytes,@textoOcr,@proveedorOcr,@proveedorIa,
                     @respuestaIaJson,@confianza,@imagenGuardada,@rutaArchivo)
              RETURNING id",
            new { usuarioId, nombreArchivo, contentType, tamanoBytes, textoOcr, proveedorOcr, proveedorIa, respuestaIaJson, confianza, imagenGuardada, rutaArchivo });
    }

    public void AsociarDocumentoMovimiento(int usuarioId, int documentoId, int movimientoId)
    {
        using var con = _db.Abrir();
        con.Execute(
            @"UPDATE documentos_movimiento_ocr SET movimiento_id=@movimientoId
              WHERE id=@documentoId AND usuario_id=@usuarioId AND movimiento_id IS NULL",
            new { usuarioId, documentoId, movimientoId });
    }

    private RegistroNaturalVm CrearVmBase(System.Data.IDbConnection con, int usuarioId) => new()
    {
        Cuentas = con.Query<Cuenta>("SELECT id,nombre,tipo,icono FROM cuentas WHERE usuario_id=@usuarioId AND activo ORDER BY nombre", new { usuarioId }).ToList(),
        Categorias = con.Query<Categoria>("SELECT id,nombre,tipo,color,icono FROM categorias WHERE usuario_id=@usuarioId AND activo ORDER BY nombre", new { usuarioId }).ToList()
    };

    private static void InterpretarPorReglas(RegistroNaturalVm vm, string texto)
    {
        var normal = Normalizar(texto);
        vm.Tipo = Regex.IsMatch(normal, @"\b(recibi|recibí|ingreso|salario|nomina|nómina|arriendo|alquiler|pago recibido|me pagaron)\b") ? "ingreso" : "gasto";
        vm.Fecha = ExtraerFecha(normal) ?? (normal.Contains("ayer") ? DateTime.Today.AddDays(-1) : DateTime.Today);
        vm.Monto = ExtraerMontoDocumento(normal);
        vm.CuentaId = SugerirCuenta(vm.Cuentas, normal);
        vm.CategoriaId = SugerirCategoria(vm.Categorias, vm.Tipo, normal);
        vm.Descripcion = CrearDescripcion(texto, vm.Tipo);
        vm.Confianza = CalcularConfianza(vm);
    }

    private async Task<bool> IntentarInterpretarConIaAsync(RegistroNaturalVm vm)
    {
        var apiKey = _config["AI:OpenAI:ApiKey"] ?? _config["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        try
        {
            var modelo = _config["AI:OpenAI:Model"] ?? "gpt-4o-mini";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var payload = new
            {
                model = modelo,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = "Eres un asistente financiero. Devuelve solo JSON valido con: tipo(gasto|ingreso), fecha(yyyy-MM-dd|null), monto(numero), descripcion, cuentaId(numero|null), categoriaId(numero|null), confianza(0-1)." },
                    new
                    {
                        role = "user",
                        content = JsonSerializer.Serialize(new
                        {
                            textoOcr = vm.TextoOcr,
                            cuentas = vm.Cuentas.Select(c => new { c.Id, c.Nombre, c.Tipo }),
                            categorias = vm.Categorias.Select(c => new { c.Id, c.Nombre, c.Tipo })
                        })
                    }
                }
            };
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return false;
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) return false;
            using var data = JsonDocument.Parse(content);
            var root = data.RootElement;

            var tipo = root.TryGetProperty("tipo", out var tipoEl) ? tipoEl.GetString() : null;
            if (tipo is "gasto" or "ingreso") vm.Tipo = tipo;
            if (root.TryGetProperty("fecha", out var fechaEl) && DateTime.TryParse(fechaEl.GetString(), out var fecha)) vm.Fecha = fecha;
            if (root.TryGetProperty("monto", out var montoEl) && montoEl.TryGetDecimal(out var monto)) vm.Monto = monto;
            if (root.TryGetProperty("descripcion", out var descEl)) vm.Descripcion = (descEl.GetString() ?? "").Trim();
            if (root.TryGetProperty("cuentaId", out var cuentaEl) && cuentaEl.ValueKind == JsonValueKind.Number) vm.CuentaId = cuentaEl.GetInt32();
            if (root.TryGetProperty("categoriaId", out var catEl) && catEl.ValueKind == JsonValueKind.Number) vm.CategoriaId = catEl.GetInt32();
            if (root.TryGetProperty("confianza", out var confEl) && confEl.TryGetDecimal(out var conf)) vm.Confianza = Math.Clamp(conf, 0, 1);

            vm.ProveedorIa = "openai";
            if (!vm.Cuentas.Any(x => x.Id == vm.CuentaId)) vm.CuentaId = null;
            if (!vm.Categorias.Any(x => x.Id == vm.CategoriaId && x.Tipo == vm.Tipo)) vm.CategoriaId = null;
            if (string.IsNullOrWhiteSpace(vm.Descripcion)) vm.Descripcion = CrearDescripcion(vm.TextoOcr, vm.Tipo);
            if (vm.Confianza <= 0) vm.Confianza = CalcularConfianza(vm);
            return vm.Monto > 0;
        }
        catch
        {
            return false;
        }
    }

    private static decimal ExtraerMonto(string texto)
    {
        var coincidencias = Regex.Matches(texto, @"(?<numero>\d[\d.,]*)(?:\s*(?<escala>mil|millon|millones))?");
        if (coincidencias.Count == 0) return 0;
        var monto = coincidencias[^1];
        var limpio = monto.Groups["numero"].Value.Replace(".", "").Replace(',', '.');
        if (!decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out var valor)) return 0;
        return monto.Groups["escala"].Value switch
        {
            "mil" => valor * 1000,
            "millon" or "millones" => valor * 1000000,
            _ => valor
        };
    }

    private static decimal ExtraerMontoDocumento(string texto)
    {
        var candidatos = new List<decimal>();
        foreach (Match match in Regex.Matches(texto, @"(?<etiqueta>total|valor|monto|pagar|importe|subtotal)?[^\d]{0,12}(?<numero>\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d{4,})(?!\d)", RegexOptions.IgnoreCase))
        {
            var valor = ParseNumero(match.Groups["numero"].Value);
            if (valor > 0) candidatos.Add(match.Groups["etiqueta"].Success ? valor * 1.1m : valor);
        }
        if (candidatos.Count == 0) return ExtraerMonto(texto);
        return Math.Round(candidatos.Max(), 2);
    }

    private static decimal ParseNumero(string valor)
    {
        var limpio = valor.Trim();
        var ultimoPunto = limpio.LastIndexOf('.');
        var ultimaComa = limpio.LastIndexOf(',');
        if (ultimoPunto >= 0 && ultimaComa >= 0)
            limpio = ultimoPunto > ultimaComa ? limpio.Replace(",", "") : limpio.Replace(".", "").Replace(',', '.');
        else if (ultimaComa >= 0)
            limpio = Regex.IsMatch(limpio, @",\d{1,2}$") ? limpio.Replace(".", "").Replace(',', '.') : limpio.Replace(",", "");
        else
            limpio = Regex.IsMatch(limpio, @"\.\d{1,2}$") ? limpio.Replace(",", "") : limpio.Replace(".", "");
        return decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static DateTime? ExtraerFecha(string texto)
    {
        var m = Regex.Match(texto, @"\b(?<d>\d{1,2})[/-](?<m>\d{1,2})[/-](?<y>\d{2,4})\b");
        if (!m.Success) return null;
        var y = int.Parse(m.Groups["y"].Value);
        if (y < 100) y += 2000;
        return DateTime.TryParse($"{y}-{m.Groups["m"].Value}-{m.Groups["d"].Value}", out var fecha) ? fecha : null;
    }

    private static int? SugerirCuenta(List<Cuenta> cuentas, string normal)
    {
        var directa = cuentas.FirstOrDefault(x => normal.Contains(Normalizar(x.Nombre)))?.Id;
        if (directa.HasValue) return directa;
        if (Regex.IsMatch(normal, @"\b(visa|mastercard|amex|credito|crédito|tc)\b"))
            return cuentas.FirstOrDefault(x => x.Tipo == "tarjeta_credito")?.Id;
        if (normal.Contains("efectivo")) return cuentas.FirstOrDefault(x => x.Tipo == "efectivo")?.Id;
        return cuentas.FirstOrDefault(x => x.Tipo != "tarjeta_credito")?.Id ?? cuentas.FirstOrDefault()?.Id;
    }

    private static int? SugerirCategoria(List<Categoria> categorias, string tipo, string normal)
    {
        var propias = categorias.Where(x => x.Tipo == tipo).ToList();
        var directa = propias.FirstOrDefault(x => normal.Contains(Normalizar(x.Nombre)))?.Id;
        if (directa.HasValue) return directa;
        var reglas = new Dictionary<string, string[]>
        {
            ["mercado"] = new[] { "supermercado", "exito", "éxito", "jumbo", "d1", "ara", "olimpica", "olímpica", "mercado" },
            ["transporte"] = new[] { "uber", "didi", "taxi", "gasolina", "combustible", "parqueadero" },
            ["restaurante"] = new[] { "restaurante", "cafe", "café", "comida", "domicilio", "rappi" },
            ["servicios"] = new[] { "energia", "energía", "agua", "internet", "telefono", "teléfono", "gas" },
            ["salario"] = new[] { "salario", "nomina", "nómina" },
            ["arriendo"] = new[] { "arriendo", "alquiler" }
        };
        foreach (var regla in reglas)
        {
            if (!regla.Value.Any(normal.Contains)) continue;
            var cat = propias.FirstOrDefault(x => Normalizar(x.Nombre).Contains(regla.Key) || regla.Key.Contains(Normalizar(x.Nombre)));
            if (cat != null) return cat.Id;
        }
        return propias.FirstOrDefault()?.Id;
    }

    private static string CrearDescripcion(string texto, string tipo)
    {
        var lineas = texto.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var comercio = lineas.FirstOrDefault(x => Regex.IsMatch(x, "[a-zA-Z]")) ?? (tipo == "ingreso" ? "Ingreso detectado" : "Gasto detectado");
        comercio = Regex.Replace(comercio, @"\s+", " ").Trim();
        return comercio.Length > 120 ? comercio[..120] : comercio;
    }

    private static decimal CalcularConfianza(RegistroNaturalVm vm)
    {
        decimal puntos = 0;
        if (vm.Monto > 0) puntos += .35m;
        if (vm.Fecha != default) puntos += .20m;
        if (vm.CuentaId.HasValue) puntos += .15m;
        if (vm.CategoriaId.HasValue) puntos += .15m;
        if (!string.IsNullOrWhiteSpace(vm.Descripcion)) puntos += .15m;
        return Math.Min(1, puntos);
    }

    private static string Normalizar(string texto) => (texto ?? "").Trim().ToLowerInvariant();

    public List<RecordatorioVm> CrearRecordatorios(int usuarioId)
    {
        using var con = _db.Abrir();
        var limite = DateTime.Today.AddDays(10);
        var lista = con.Query<RecordatorioVm>(
            @"SELECT 'periodico' tipo,c.icono,g.nombre titulo,
              CONCAT(CASE WHEN g.tipo='ingreso' THEN 'Ingreso esperado ' ELSE 'Pago estimado ' END,TO_CHAR(g.monto_estimado,'FM999G999G999')) detalle,g.proxima_fecha fecha,
              CONCAT('Recordatorio: ',g.nombre,' por ',TO_CHAR(g.monto_estimado,'FM999G999G999'),' vence el ',TO_CHAR(g.proxima_fecha,'DD/MM/YYYY'),'.') mensaje,
              CONCAT(CASE WHEN g.tipo='ingreso' THEN 'Tu ingreso recurrente ' ELSE 'Tu gasto recurrente ' END,g.nombre,' por ',TO_CHAR(g.monto_estimado,'FM999G999G999'),' vence el ',TO_CHAR(g.proxima_fecha,'DD/MM/YYYY'),'.') AS ""MensajeAdmin""
              FROM gastos_periodicos g JOIN categorias c ON c.id=g.categoria_id
              WHERE g.usuario_id=@usuarioId AND g.activo AND g.proxima_fecha<=@limite ORDER BY g.proxima_fecha",
            new { usuarioId, limite }).ToList();
        lista.AddRange(con.Query<RecordatorioVm>(
            @"SELECT 'prestamo' tipo,'bi-cash-stack' icono,CONCAT('Cobro a ',pe.nombre) titulo,
              CONCAT('Prestamo por ',TO_CHAR(p.capital,'FM999G999G999')) detalle,
              COALESCE(p.fecha_pago_capital,DATE_TRUNC('month',CURRENT_DATE)::date + (LEAST(COALESCE(p.dia_pago_interes,1),28)-1)) fecha,
              pe.telefono,pe.email,
              CONCAT('Hola ',pe.nombre,', te recordamos el pago pendiente de tu prestamo. Gracias.') mensaje,
              CONCAT('Debes cobrar a ',pe.nombre,' el prestamo programado para el ',TO_CHAR(COALESCE(p.fecha_pago_capital,DATE_TRUNC('month',CURRENT_DATE)::date + (LEAST(COALESCE(p.dia_pago_interes,1),28)-1)),'DD/MM/YYYY'),'. Telefono: ',COALESCE(pe.telefono,'sin telefono'),'.') AS ""MensajeAdmin""
              FROM prestamos p JOIN personas pe ON pe.id=p.persona_id
              WHERE p.usuario_id=@usuarioId AND p.estado='activo'
              AND COALESCE(p.fecha_pago_capital,DATE_TRUNC('month',CURRENT_DATE)::date + (LEAST(COALESCE(p.dia_pago_interes,1),28)-1))<=@limite",
            new { usuarioId, limite }));
        lista.AddRange(con.Query<RecordatorioVm>(
            @"SELECT 'inversion' tipo,i.icono,i.nombre titulo,
              CONCAT('Retorno esperado por ',TO_CHAR(i.capital_inicial,'FM999G999G999')) detalle,
              i.fecha_retorno fecha,
              CONCAT('La inversion ',i.nombre,' tiene retorno esperado el ',TO_CHAR(i.fecha_retorno,'DD/MM/YYYY'),'.') mensaje,
              CONCAT('Tu inversion ',i.nombre,' tiene retorno esperado el ',TO_CHAR(i.fecha_retorno,'DD/MM/YYYY'),'.') AS ""MensajeAdmin""
              FROM inversiones i WHERE i.usuario_id=@usuarioId AND i.estado='activa'
              AND i.fecha_retorno IS NOT NULL AND i.fecha_retorno<=@limite
              ORDER BY i.fecha_retorno", new { usuarioId, limite }));
        return lista.OrderBy(x => x.Fecha).ToList();
    }
}
