using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;

namespace FinanzasPersonales.Web.Services;

public class AsistenteFinancieroService
{
    private readonly Db _db;
    public AsistenteFinancieroService(Db db) => _db = db;

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
        var recomendaciones = new List<RecomendacionFinancieraVm>();

        if (ingresos > 0 && gastos > ingresos)
            recomendaciones.Add(new() { Tipo="danger", Icono="bi-exclamation-triangle", Titulo="Estas gastando mas de lo que recibes", Detalle=$"El deficit del periodo es {(gastos-ingresos):C0}.", Accion="Revisar movimientos", Controller="Movimientos" });
        else if (ingresos > 0 && (ingresos - gastos) / ingresos < .10m)
            recomendaciones.Add(new() { Tipo="warning", Icono="bi-piggy-bank", Titulo="Tu margen de ahorro es menor al 10%", Detalle="Un pequeño ajuste en gastos variables puede fortalecer tu liquidez.", Accion="Revisar presupuestos", Controller="Presupuestos" });
        else if (ingresos > 0)
            recomendaciones.Add(new() { Tipo="success", Icono="bi-graph-up-arrow", Titulo="Buen resultado mensual", Detalle=$"Has conservado {(ingresos-gastos):C0} durante el periodo.", Accion="Aportar a una meta", Controller="Metas" });

        if (gastosAnterior > 0 && gastos > gastosAnterior * 1.20m)
            recomendaciones.Add(new() { Tipo="warning", Icono="bi-arrow-up-right", Titulo="Tus gastos aumentaron mas de 20%", Detalle=$"Gastaste {(gastos-gastosAnterior):C0} mas que el mes anterior.", Accion="Ver comparacion", Controller="Dashboard" });

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
        var vm = new RegistroNaturalVm { Texto = texto?.Trim() ?? "" };
        vm.Cuentas = con.Query<Cuenta>("SELECT id,nombre,tipo,icono FROM cuentas WHERE usuario_id=@usuarioId AND activo ORDER BY nombre", new { usuarioId }).ToList();
        vm.Categorias = con.Query<Categoria>("SELECT id,nombre,tipo,color,icono FROM categorias WHERE usuario_id=@usuarioId AND activo ORDER BY nombre", new { usuarioId }).ToList();
        if (string.IsNullOrWhiteSpace(vm.Texto)) return vm;

        var normal = vm.Texto.ToLowerInvariant();
        vm.Tipo = Regex.IsMatch(normal, @"\b(recibi|recibí|ingreso|salario|pago recibido|me pagaron)\b") ? "ingreso" : "gasto";
        vm.Fecha = normal.Contains("ayer") ? DateTime.Today.AddDays(-1) : DateTime.Today;
        vm.Monto = ExtraerMonto(normal);
        vm.CuentaId = vm.Cuentas.FirstOrDefault(x => normal.Contains(x.Nombre.ToLowerInvariant()))?.Id;
        vm.CategoriaId = vm.Categorias.FirstOrDefault(x => x.Tipo == vm.Tipo && normal.Contains(x.Nombre.ToLowerInvariant()))?.Id;
        vm.Descripcion = Regex.Replace(vm.Texto, @"\d[\d.,]*", "").Trim(' ', '.', ',');
        vm.Interpretado = vm.Monto > 0;
        return vm;
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
