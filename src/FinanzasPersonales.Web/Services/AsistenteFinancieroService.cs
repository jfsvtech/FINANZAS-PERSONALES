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
