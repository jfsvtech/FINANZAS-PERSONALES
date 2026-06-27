using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.DataProtection;

namespace FinanzasPersonales.Web.Services;

public class WhatsAppService
{
    private readonly IConfiguration _config;
    private readonly Db _db;
    private readonly IDataProtector _protector;
    private readonly HttpClient _http;
    private readonly ILogger<WhatsAppService> _logger;

    public WhatsAppService(IConfiguration config, Db db, IDataProtectionProvider dataProtection, HttpClient http, ILogger<WhatsAppService> logger)
    {
        _config = config;
        _db = db;
        _protector = dataProtection.CreateProtector("FinanzasPersonales.WhatsAppSettings");
        _http = http;
        _logger = logger;
    }

    public WhatsAppSettings ObtenerConfiguracion()
    {
        var dbSettings = ObtenerDesdeBaseDatos();
        if (dbSettings.Configurado || !string.IsNullOrWhiteSpace(dbSettings.PhoneNumberId))
            return dbSettings;

        return new WhatsAppSettings
        {
            PhoneNumberId = _config["Notifications:WhatsApp:PhoneNumberId"] ?? "",
            AccessToken = _config["Notifications:WhatsApp:AccessToken"] ?? "",
            GraphApiVersion = _config["Notifications:WhatsApp:GraphApiVersion"] ?? "v23.0",
            AdminPhone = _config["Notifications:WhatsApp:AdminPhone"] ?? "",
            TemplateName = _config["Notifications:WhatsApp:TemplateName"] ?? "",
            TemplateLanguage = _config["Notifications:WhatsApp:TemplateLanguage"] ?? "es"
        };
    }

    public void GuardarConfiguracion(WhatsAppSettings settings, bool actualizarToken)
    {
        using var con = _db.Abrir();
        Guardar(con, "WhatsApp:PhoneNumberId", settings.PhoneNumberId, false);
        Guardar(con, "WhatsApp:GraphApiVersion", string.IsNullOrWhiteSpace(settings.GraphApiVersion) ? "v23.0" : settings.GraphApiVersion, false);
        Guardar(con, "WhatsApp:AdminPhone", NormalizarTelefono(settings.AdminPhone), false);
        Guardar(con, "WhatsApp:TemplateName", settings.TemplateName, false);
        Guardar(con, "WhatsApp:TemplateLanguage", string.IsNullOrWhiteSpace(settings.TemplateLanguage) ? "es" : settings.TemplateLanguage, false);
        if (actualizarToken)
            Guardar(con, "WhatsApp:AccessToken", _protector.Protect(settings.AccessToken), true);
    }

    public async Task<WhatsAppSendResult> EnviarTextoAsync(string telefono, string mensaje)
    {
        var settings = ObtenerConfiguracion();
        if (!settings.Configurado)
            return new() { Ok = false, Message = "WhatsApp no esta configurado completamente." };

        var to = NormalizarTelefono(telefono);
        if (string.IsNullOrWhiteSpace(to))
            return new() { Ok = false, Message = "Telefono destino invalido." };

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to,
            type = "text",
            text = new { preview_url = false, body = mensaje }
        };
        return await EnviarPayloadAsync(settings, payload);
    }

    public async Task<WhatsAppSendResult> EnviarPlantillaAsync(string telefono, params string[] parametros)
    {
        var settings = ObtenerConfiguracion();
        if (!settings.Configurado)
            return new() { Ok = false, Message = "WhatsApp no esta configurado completamente." };
        if (!settings.PlantillaConfigurada)
            return new() { Ok = false, Message = "No hay plantilla de WhatsApp configurada." };

        var to = NormalizarTelefono(telefono);
        if (string.IsNullOrWhiteSpace(to))
            return new() { Ok = false, Message = "Telefono destino invalido." };

        var payload = new
        {
            messaging_product = "whatsapp",
            to,
            type = "template",
            template = new
            {
                name = settings.TemplateName,
                language = new { code = settings.TemplateLanguage },
                components = new[]
                {
                    new
                    {
                        type = "body",
                        parameters = parametros.Select(x => new { type = "text", text = x }).ToArray()
                    }
                }
            }
        };
        return await EnviarPayloadAsync(settings, payload);
    }

    public async Task<WhatsAppSendResult> EnviarPruebaAsync(string telefono)
    {
        return await EnviarTextoAsync(telefono,
            "Prueba de WhatsApp desde Finanzas Personales. La conexion con WhatsApp Business Cloud API esta funcionando.");
    }

    private async Task<WhatsAppSendResult> EnviarPayloadAsync(WhatsAppSettings settings, object payload)
    {
        var version = string.IsNullOrWhiteSpace(settings.GraphApiVersion) ? "v23.0" : settings.GraphApiVersion.Trim();
        var url = $"https://graph.facebook.com/{version}/{settings.PhoneNumberId}/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return new() { Ok = true, Message = "Mensaje enviado correctamente.", ProviderResponse = body };

            _logger.LogWarning("WhatsApp API respondio {Status}: {Body}", response.StatusCode, body);
            return new() { Ok = false, Message = $"WhatsApp rechazo el envio ({(int)response.StatusCode}).", ProviderResponse = body };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando mensaje por WhatsApp.");
            return new() { Ok = false, Message = "No se pudo conectar con WhatsApp: " + ex.Message };
        }
    }

    public static string NormalizarTelefono(string? telefono)
    {
        var digits = new string((telefono ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10 && digits.StartsWith("3"))
            return "57" + digits;
        return digits;
    }

    private WhatsAppSettings ObtenerDesdeBaseDatos()
    {
        try
        {
            using var con = _db.Abrir();
            var filas = con.Query<(string Clave, string? Valor, bool Protegido)>(
                "SELECT clave, valor, protegido FROM configuraciones_sistema WHERE clave LIKE 'WhatsApp:%'")
                .ToDictionary(x => x.Clave, x => x, StringComparer.OrdinalIgnoreCase);

            string Leer(string clave)
            {
                if (!filas.TryGetValue(clave, out var fila) || string.IsNullOrWhiteSpace(fila.Valor))
                    return "";
                if (!fila.Protegido)
                    return fila.Valor;
                try { return _protector.Unprotect(fila.Valor); }
                catch { return ""; }
            }

            return new WhatsAppSettings
            {
                PhoneNumberId = Leer("WhatsApp:PhoneNumberId"),
                AccessToken = Leer("WhatsApp:AccessToken"),
                GraphApiVersion = string.IsNullOrWhiteSpace(Leer("WhatsApp:GraphApiVersion")) ? "v23.0" : Leer("WhatsApp:GraphApiVersion"),
                AdminPhone = Leer("WhatsApp:AdminPhone"),
                TemplateName = Leer("WhatsApp:TemplateName"),
                TemplateLanguage = string.IsNullOrWhiteSpace(Leer("WhatsApp:TemplateLanguage")) ? "es" : Leer("WhatsApp:TemplateLanguage")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer configuracion WhatsApp desde base de datos.");
            return new WhatsAppSettings();
        }
    }

    private static void Guardar(System.Data.IDbConnection con, string clave, string? valor, bool protegido)
    {
        con.Execute(
            @"INSERT INTO configuraciones_sistema(clave,valor,protegido,actualizado_en)
              VALUES(@clave,@valor,@protegido,NOW())
              ON CONFLICT (clave) DO UPDATE
              SET valor=EXCLUDED.valor, protegido=EXCLUDED.protegido, actualizado_en=NOW()",
            new { clave, valor, protegido });
    }
}
