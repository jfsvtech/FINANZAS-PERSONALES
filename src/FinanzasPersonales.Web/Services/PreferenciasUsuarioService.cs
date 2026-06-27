using System.Globalization;
using System.Net.Http.Json;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;

namespace FinanzasPersonales.Web.Services;

public class PreferenciasUsuarioService
{
    private readonly Db _db;
    private readonly HttpClient _http;
    private readonly ILogger<PreferenciasUsuarioService> _logger;

    public PreferenciasUsuarioService(Db db, HttpClient http, ILogger<PreferenciasUsuarioService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public static readonly List<IdiomaDisponible> IdiomasSoportados = new()
    {
        new() { Codigo = "es", Nombre = "Espanol", Cultura = "es-CO" },
        new() { Codigo = "en", Nombre = "English", Cultura = "en-US" },
        new() { Codigo = "pt", Nombre = "Portugues", Cultura = "pt-BR" }
    };

    public static readonly List<Moneda> MonedasSoportadas = new()
    {
        new() { Codigo = "COP", Nombre = "Peso colombiano", Simbolo = "$", Decimales = 0, Cultura = "es-CO" },
        new() { Codigo = "USD", Nombre = "US Dollar", Simbolo = "US$", Decimales = 2, Cultura = "en-US" },
        new() { Codigo = "EUR", Nombre = "Euro", Simbolo = "EUR", Decimales = 2, Cultura = "es-ES" },
        new() { Codigo = "BRL", Nombre = "Real brasileiro", Simbolo = "R$", Decimales = 2, Cultura = "pt-BR" }
    };

    public List<Moneda> Monedas() => MonedasSoportadas;
    public List<IdiomaDisponible> Idiomas() => IdiomasSoportados;

    public PreferenciasUsuario Obtener(int usuarioId)
    {
        using var con = _db.Abrir();
        var pref = con.QueryFirstOrDefault<PreferenciasUsuario>(
            @"SELECT idioma, moneda_codigo AS MonedaCodigo, zona_horaria AS ZonaHoraria
              FROM usuarios WHERE id=@usuarioId", new { usuarioId });
        return Normalizar(pref);
    }

    public string FormatoMoneda(decimal monto, int usuarioId)
    {
        var pref = Obtener(usuarioId);
        return FormatoMoneda(monto, pref.MonedaCodigo, pref.Idioma);
    }

    public string FormatoMoneda(decimal monto, string monedaCodigo, string idioma = "es")
    {
        var moneda = MonedasSoportadas.FirstOrDefault(x => x.Codigo.Equals(monedaCodigo, StringComparison.OrdinalIgnoreCase))
            ?? MonedasSoportadas[0];
        var culturaCodigo = IdiomasSoportados.FirstOrDefault(x => x.Codigo == idioma)?.Cultura ?? moneda.Cultura;
        var cultura = CultureInfo.GetCultureInfo(culturaCodigo);
        var formato = (NumberFormatInfo)cultura.NumberFormat.Clone();
        formato.CurrencySymbol = moneda.Simbolo;
        formato.CurrencyDecimalDigits = moneda.Decimales;
        return monto.ToString("C", formato);
    }

    public async Task<ConversionMoneda> ConvertirAsync(decimal monto, string monedaOrigen, string monedaDestino, DateTime fecha, decimal? tasaManual = null)
    {
        monedaOrigen = NormalizarMoneda(monedaOrigen);
        monedaDestino = NormalizarMoneda(monedaDestino);
        if (monedaOrigen == monedaDestino)
            return new ConversionMoneda(monto, monto, monedaOrigen, monedaDestino, 1, "misma_moneda");

        var tasa = tasaManual.GetValueOrDefault();
        var fuente = "manual";
        if (tasa <= 0)
        {
            var almacenada = ObtenerTasa(fecha.Date, monedaOrigen, monedaDestino);
            if (almacenada.HasValue)
            {
                tasa = almacenada.Value;
                fuente = "base_datos";
            }
            else
            {
                var consultada = await ConsultarTasaAsync(fecha.Date, monedaOrigen, monedaDestino);
                if (consultada.HasValue)
                {
                    tasa = consultada.Value;
                    fuente = "api";
                    GuardarTasa(fecha.Date, monedaOrigen, monedaDestino, tasa, "api");
                }
            }
        }

        if (tasa <= 0)
            throw new InvalidOperationException($"No hay tasa de conversion {monedaOrigen}->{monedaDestino} para {fecha:yyyy-MM-dd}. Ingresa una tasa manual.");

        return new ConversionMoneda(monto, Math.Round(monto * tasa, 2), monedaOrigen, monedaDestino, tasa, fuente);
    }

    private decimal? ObtenerTasa(DateTime fecha, string origen, string destino)
    {
        using var con = _db.Abrir();
        return con.ExecuteScalar<decimal?>(
            @"SELECT tasa FROM tasas_cambio
              WHERE fecha=@fecha AND moneda_origen=@origen AND moneda_destino=@destino",
            new { fecha, origen, destino });
    }

    private void GuardarTasa(DateTime fecha, string origen, string destino, decimal tasa, string fuente)
    {
        using var con = _db.Abrir();
        con.Execute(
            @"INSERT INTO tasas_cambio(fecha,moneda_origen,moneda_destino,tasa,fuente)
              VALUES(@fecha,@origen,@destino,@tasa,@fuente)
              ON CONFLICT(fecha,moneda_origen,moneda_destino)
              DO UPDATE SET tasa=EXCLUDED.tasa, fuente=EXCLUDED.fuente, creado_en=NOW()",
            new { fecha, origen, destino, tasa, fuente });
    }

    private async Task<decimal?> ConsultarTasaAsync(DateTime fecha, string origen, string destino)
    {
        try
        {
            var url = $"https://open.er-api.com/v6/latest/{origen}";
            var data = await _http.GetFromJsonAsync<ExchangeRateResponse>(url);
            if (data?.Rates != null && data.Rates.TryGetValue(destino, out var tasa) && tasa > 0)
                return tasa;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo consultar tasa {Origen}->{Destino} para {Fecha}.", origen, destino, fecha);
        }
        return null;
    }

    public static string NormalizarMoneda(string? moneda) =>
        MonedasSoportadas.Any(x => x.Codigo.Equals(moneda, StringComparison.OrdinalIgnoreCase))
            ? moneda!.Trim().ToUpperInvariant()
            : "COP";

    public static string NormalizarIdioma(string? idioma) =>
        IdiomasSoportados.Any(x => x.Codigo.Equals(idioma, StringComparison.OrdinalIgnoreCase))
            ? idioma!.Trim().ToLowerInvariant()
            : "es";

    private static PreferenciasUsuario Normalizar(PreferenciasUsuario? pref) => new()
    {
        Idioma = NormalizarIdioma(pref?.Idioma),
        MonedaCodigo = NormalizarMoneda(pref?.MonedaCodigo),
        ZonaHoraria = string.IsNullOrWhiteSpace(pref?.ZonaHoraria) ? "America/Bogota" : pref!.ZonaHoraria
    };

    private sealed class ExchangeRateResponse
    {
        public Dictionary<string, decimal>? Rates { get; set; }
    }
}

public class PreferenciasUsuario
{
    public string Idioma { get; set; } = "es";
    public string MonedaCodigo { get; set; } = "COP";
    public string ZonaHoraria { get; set; } = "America/Bogota";
}

public record ConversionMoneda(decimal MontoOriginal, decimal MontoBase, string MonedaOrigen, string MonedaDestino, decimal Tasa, string Fuente);
