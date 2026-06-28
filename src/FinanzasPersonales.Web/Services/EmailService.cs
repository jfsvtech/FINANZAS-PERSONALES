using System.Net;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.DataProtection;

namespace FinanzasPersonales.Web.Services;

public class EmailService
{
    private readonly IConfiguration _config;
    private readonly Db _db;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _apiProtector;
    private readonly HttpClient _http;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, Db db, IDataProtectionProvider dataProtection, HttpClient http, ILogger<EmailService> logger)
    {
        _config = config;
        _db = db;
        _protector = dataProtection.CreateProtector("FinanzasPersonales.SmtpSettings");
        _apiProtector = dataProtection.CreateProtector("FinanzasPersonales.EmailApiSettings");
        _http = http;
        _logger = logger;
    }

    public bool Configurado => ProveedorEnvio() switch
    {
        "gmailapi" => ObtenerGmailApiConfiguracion().Configurado,
        "resend" => ObtenerApiConfiguracion().Configurado,
        _ => ObtenerConfiguracion().Configurado || ObtenerGmailApiConfiguracion().Configurado || ObtenerApiConfiguracion().Configurado
    };

    public EmailApiSettings ObtenerApiConfiguracion()
    {
        return new EmailApiSettings
        {
            Provider = LeerConfig("Notifications:EmailApi:Provider", "EMAIL_API_PROVIDER") ?? "resend",
            ApiKey = LeerConfig("Notifications:EmailApi:ApiKey", "EMAIL_API_KEY") ?? "",
            FromEmail = LeerConfig("Notifications:EmailApi:FromEmail", "EMAIL_API_FROM_EMAIL") ?? "",
            FromName = LeerConfig("Notifications:EmailApi:FromName", "EMAIL_API_FROM_NAME") ?? "Finanzas Personales"
        };
    }

    public GmailApiSettings ObtenerGmailApiConfiguracion()
    {
        return new GmailApiSettings
        {
            ClientId = LeerConfig("Notifications:GmailApi:ClientId", "GMAIL_CLIENT_ID") ?? "",
            ClientSecret = LeerConfig("Notifications:GmailApi:ClientSecret", "GMAIL_CLIENT_SECRET") ?? "",
            RefreshToken = LeerConfig("Notifications:GmailApi:RefreshToken", "GMAIL_REFRESH_TOKEN") ?? "",
            FromEmail = LeerConfig("Notifications:GmailApi:FromEmail", "GMAIL_FROM_EMAIL") ?? "",
            FromName = LeerConfig("Notifications:GmailApi:FromName", "GMAIL_FROM_NAME") ?? "Finanzas Personales"
        };
    }


    public SmtpSettings ObtenerConfiguracion()
    {
        var portRaw = LeerConfig("Notifications:Smtp:Port", "SMTP_PORT");
        var sslRaw = LeerConfig("Notifications:Smtp:EnableSsl", "SMTP_ENABLE_SSL");
        return new SmtpSettings
        {
            Host = LeerConfig("Notifications:Smtp:Host", "SMTP_HOST") ?? "",
            Port = int.TryParse(portRaw, out var port) ? port : 587,
            User = LeerConfig("Notifications:Smtp:User", "SMTP_USER") ?? "",
            Password = LeerConfig("Notifications:Smtp:Password", "SMTP_PASSWORD") ?? "",
            From = LeerConfig("Notifications:Smtp:From", "SMTP_FROM") ?? "",
            EnableSsl = !bool.TryParse(sslRaw, out var ssl) || ssl
        };
    }

    public SmtpDiagnostico ObtenerDiagnostico()
    {
        var smtp = ObtenerConfiguracion();
        var api = ObtenerApiConfiguracion();
        var gmail = ObtenerGmailApiConfiguracion();
        var proveedor = ProveedorEnvio();
        return new SmtpDiagnostico
        {
            TieneConfiguracionBaseDatos = false,
            PasswordCifradaEnBaseDatos = false,
            PasswordDescifrable = true,
            Mensaje = proveedor == "gmailapi" && gmail.Configurado
                ? "Gmail API configurado desde variables de entorno. Envia por Gmail usando HTTPS, sin SMTP ni DataProtection."
                : proveedor == "gmailapi"
                    ? "Proveedor configurado como Gmail API, pero faltan variables GMAIL_CLIENT_ID, GMAIL_CLIENT_SECRET, GMAIL_REFRESH_TOKEN y GMAIL_FROM_EMAIL."
                    : proveedor == "resend" && api.Configurado
                ? "Correo API Resend configurado desde variables de entorno. No depende de DataProtection ni de la base de datos."
                : proveedor == "resend"
                    ? "Proveedor configurado como Resend, pero faltan variables EMAIL_API_KEY y EMAIL_API_FROM_EMAIL."
                    : smtp.Configurado
                ? "Correo SMTP configurado desde variables de entorno. No depende de DataProtection ni de la base de datos."
                : api.Configurado
                    ? "Correo API configurado desde variables de entorno. No depende de DataProtection ni de la base de datos."
                    : "Correo no configurado. Define las variables de entorno SMTP en Railway."
        };
    }

    public void GuardarConfiguracion(SmtpSettings settings, bool actualizarPassword)
    {
        throw new InvalidOperationException("La configuracion de correo se administra por variables de entorno.");
    }

    public void GuardarApiConfiguracion(EmailApiSettings settings, bool actualizarApiKey)
    {
        throw new InvalidOperationException("La configuracion de correo API se administra por variables de entorno.");
    }

    public async Task<bool> EnviarPruebaAsync(string destinatario)
    {
        return await EnviarAsync(destinatario, "Prueba de correo - Finanzas Personales",
            """
            <div style="font-family:Arial,sans-serif;max-width:620px;margin:auto;padding:24px;background:#F5F5F7;color:#1C1C1E">
              <div style="background:#fff;border:1px solid #E5E7EB;border-radius:16px;padding:28px">
                <div style="color:#7C3AED;font-weight:800;letter-spacing:.08em;text-transform:uppercase;font-size:12px">Finanzas Personales</div>
                <h2 style="margin:8px 0 10px;color:#1C1C1E">Correo configurado correctamente</h2>
                <p>Esta es una prueba de envio SMTP desde el panel administrativo.</p>
              </div>
            </div>
            """);
    }

    public async Task<bool> EnviarAsync(string destinatario, string asunto, string html)
    {
        if (ProveedorEnvio() == "gmailapi")
        {
            var gmail = ObtenerGmailApiConfiguracion();
            if (gmail.Configurado)
                return await EnviarPorGmailApiAsync(gmail, destinatario, asunto, html);
            _logger.LogWarning("Proveedor Gmail API seleccionado, pero no esta configurado.");
            return false;
        }

        if (ProveedorEnvio() == "resend")
        {
            var apiSettings = ObtenerApiConfiguracion();
            if (apiSettings.Configurado)
                return await EnviarPorApiAsync(apiSettings, destinatario, asunto, html);
            _logger.LogWarning("Proveedor de correo Resend seleccionado, pero no esta configurado.");
            return false;
        }

        var settings = ObtenerConfiguracion();
        if (settings.Configurado)
            return await EnviarPorSmtpAsync(settings, destinatario, asunto, html);

        var gmailSettings = ObtenerGmailApiConfiguracion();
        if (gmailSettings.Configurado)
            return await EnviarPorGmailApiAsync(gmailSettings, destinatario, asunto, html);

        var api = ObtenerApiConfiguracion();
        if (api.Configurado)
            return await EnviarPorApiAsync(api, destinatario, asunto, html);

        _logger.LogWarning("Correo no configurado. No se envio el correo '{Subject}' a {Email}.", asunto, destinatario);
        return false;
    }

    private async Task<bool> EnviarPorGmailApiAsync(GmailApiSettings settings, string destinatario, string asunto, string html)
    {
        var token = await ObtenerAccessTokenGmailAsync(settings);
        var raw = CrearMensajeGmailRaw(settings, destinatario, asunto, html);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(JsonSerializer.Serialize(new { raw }), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (res.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : "";
                _logger.LogInformation("Correo enviado por Gmail API a {Email}. GmailMessageId={MessageId}", destinatario, id);
            }
            catch
            {
                _logger.LogInformation("Correo enviado por Gmail API a {Email}. Respuesta={Body}", destinatario, body);
            }
            return true;
        }

        _logger.LogError("Error Gmail API enviando '{Subject}' a {Email}. Status={Status}. Body={Body}",
            asunto, destinatario, (int)res.StatusCode, body);
        throw new InvalidOperationException($"No se pudo enviar por Gmail API. HTTP {(int)res.StatusCode}: {body}");
    }

    private async Task<string> ObtenerAccessTokenGmailAsync(GmailApiSettings settings)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["refresh_token"] = settings.RefreshToken,
            ["grant_type"] = "refresh_token"
        });
        using var res = await _http.PostAsync("https://oauth2.googleapis.com/token", content);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("Error obteniendo access token Gmail API. Status={Status}. Body={Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException($"No se pudo obtener token de Gmail API. HTTP {(int)res.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Google no devolvio access_token.");
    }

    private static string CrearMensajeGmailRaw(GmailApiSettings settings, string destinatario, string asunto, string html)
    {
        static string Header(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        static string TextoPlano(string value)
        {
            var sinTags = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(sinTags, "\\s+", " ")).Trim();
        }
        var from = string.IsNullOrWhiteSpace(settings.FromName)
            ? settings.FromEmail
            : $"{settings.FromName} <{settings.FromEmail}>";
        var boundary = "----=_FinanzasPersonales_" + Guid.NewGuid().ToString("N");
        var texto = TextoPlano(html);
        var mime = new StringBuilder()
            .Append("From: ").AppendLine(from)
            .Append("To: ").AppendLine(destinatario)
            .Append("Subject: =?UTF-8?B?").Append(Header(asunto)).AppendLine("?=")
            .Append("Date: ").AppendLine(DateTimeOffset.UtcNow.ToString("r", System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine("MIME-Version: 1.0")
            .Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).AppendLine("\"")
            .AppendLine()
            .Append("--").AppendLine(boundary)
            .AppendLine("Content-Type: text/plain; charset=UTF-8")
            .AppendLine("Content-Transfer-Encoding: 8bit")
            .AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(texto) ? "Finanzas Personales" : texto)
            .Append("--").AppendLine(boundary)
            .AppendLine("Content-Type: text/html; charset=UTF-8")
            .AppendLine("Content-Transfer-Encoding: 8bit")
            .AppendLine()
            .AppendLine(html)
            .Append("--").Append(boundary).AppendLine("--")
            .ToString();

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(mime))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private string ProveedorEnvio()
    {
        var proveedor = LeerConfig("Notifications:Email:Provider", "EMAIL_PROVIDER")?.Trim().ToLowerInvariant();
        return proveedor switch
        {
            "gmailapi" or "gmail-api" or "gmail_api" => "gmailapi",
            "resend" or "api" => "resend",
            _ => "smtp"
        };
    }

    private async Task<bool> EnviarPorSmtpAsync(SmtpSettings settings, string destinatario, string asunto, string html)
    {
        if (!settings.Configurado)
        {
            _logger.LogWarning("SMTP no configurado. No se envio el correo '{Subject}' a {Email}.", asunto, destinatario);
            return false;
        }

        var from = string.IsNullOrWhiteSpace(settings.From) ? settings.User : settings.From;
        using var mensaje = new MailMessage
        {
            From = new MailAddress(from, "Finanzas Personales"),
            Subject = asunto,
            Body = html,
            IsBodyHtml = true
        };
        mensaje.To.Add(new MailAddress(destinatario));

        using var smtp = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            Credentials = new NetworkCredential(settings.User, settings.Password),
            Timeout = 10000
        };

        try
        {
            await smtp.SendMailAsync(mensaje);
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex,
                "Error SMTP enviando '{Subject}' a {Email}. Host={Host}, Port={Port}, Ssl={Ssl}, Status={StatusCode}",
                asunto, destinatario, settings.Host, settings.Port, settings.EnableSsl, ex.StatusCode);
            throw new InvalidOperationException(CrearMensajeDiagnosticoSmtp(settings, ex), ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error enviando correo '{Subject}' a {Email}. Host={Host}, Port={Port}, Ssl={Ssl}",
                asunto, destinatario, settings.Host, settings.Port, settings.EnableSsl);
            throw new InvalidOperationException(CrearMensajeDiagnosticoSmtp(settings, ex), ex);
        }

        return true;
    }

    private string? LeerConfig(string claveDotNet, string claveCorta)
    {
        var valor = _config[claveDotNet];
        if (!string.IsNullOrWhiteSpace(valor)) return valor;
        valor = _config[claveCorta];
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }

    private async Task<bool> EnviarPorApiAsync(EmailApiSettings settings, string destinatario, string asunto, string html)
    {
        if (!settings.Provider.Equals("resend", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proveedor de correo por API no soportado. Por ahora usa Resend.");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        var payload = new
        {
            from = $"{settings.FromName} <{settings.FromEmail}>",
            to = new[] { destinatario },
            subject = asunto,
            html
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var res = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
                return true;

            _logger.LogError("Error API correo {Provider}. Status={Status}. Body={Body}", settings.Provider, (int)res.StatusCode, body);
            throw new InvalidOperationException($"No se pudo enviar por API de correo ({settings.Provider}). HTTP {(int)res.StatusCode}: {body}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Error enviando correo por API {Provider} a {Email}.", settings.Provider, destinatario);
            throw new InvalidOperationException("No se pudo conectar con el proveedor de correo por API HTTPS. Detalle: " + ex.Message, ex);
        }
    }

    private static string CrearMensajeDiagnosticoSmtp(SmtpSettings settings, Exception ex)
    {
        var detalle = ex.InnerException?.Message ?? ex.Message;
        var redNoDisponible = detalle.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase)
            || detalle.Contains("No route to host", StringComparison.OrdinalIgnoreCase)
            || detalle.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase);
        var proveedor = settings.Host.Contains("gmail", StringComparison.OrdinalIgnoreCase)
            ? " Para Gmail debes usar una contrasena de aplicacion de Google, tener la verificacion en 2 pasos activa y usar smtp.gmail.com con puerto 587 y SSL/TLS activo."
            : "";
        var red = redNoDisponible
            ? " El servidor publicado no puede abrir conexion de red hacia el SMTP. En plataformas cloud esto suele pasar porque el puerto SMTP saliente esta bloqueado o la ruta IPv6/SMTP no esta disponible. En Railway normalmente es mas confiable enviar correos por API HTTPS (Resend, SendGrid, Brevo, Mailgun) en vez de SMTP directo."
            : "";

        return $"No se pudo conectar o autenticar con el servidor SMTP ({settings.Host}:{settings.Port}). Detalle: {detalle}.{proveedor}{red}";
    }

    private SmtpSettings ObtenerDesdeBaseDatos()
    {
        try
        {
            using var con = _db.Abrir();
            var filas = con.Query<(string Clave, string? Valor, bool Protegido)>(
                "SELECT clave, valor, protegido FROM configuraciones_sistema WHERE clave LIKE 'Smtp:%'")
                .ToDictionary(x => x.Clave, x => x, StringComparer.OrdinalIgnoreCase);

            string Leer(string clave)
            {
                if (!filas.TryGetValue(clave, out var fila) || string.IsNullOrWhiteSpace(fila.Valor))
                    return "";
                if (!fila.Protegido)
                    return fila.Valor;
                try { return _protector.Unprotect(fila.Valor); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo descifrar la configuracion protegida {Clave}. Revisa persistencia de DataProtection.", clave);
                    return "";
                }
            }

            return new SmtpSettings
            {
                Host = Leer("Smtp:Host"),
                Port = int.TryParse(Leer("Smtp:Port"), out var port) ? port : 587,
                User = Leer("Smtp:User"),
                Password = Leer("Smtp:Password"),
                From = Leer("Smtp:From"),
                EnableSsl = !bool.TryParse(Leer("Smtp:EnableSsl"), out var ssl) || ssl
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer configuracion SMTP desde base de datos.");
            return new SmtpSettings();
        }
    }

    private EmailApiSettings ObtenerApiDesdeBaseDatos()
    {
        try
        {
            using var con = _db.Abrir();
            var filas = con.Query<(string Clave, string? Valor, bool Protegido)>(
                "SELECT clave, valor, protegido FROM configuraciones_sistema WHERE clave LIKE 'EmailApi:%'")
                .ToDictionary(x => x.Clave, x => x, StringComparer.OrdinalIgnoreCase);

            string Leer(string clave)
            {
                if (!filas.TryGetValue(clave, out var fila) || string.IsNullOrWhiteSpace(fila.Valor))
                    return "";
                if (!fila.Protegido)
                    return fila.Valor;
                try { return _apiProtector.Unprotect(fila.Valor); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo descifrar la configuracion protegida {Clave}. Revisa persistencia de DataProtection.", clave);
                    return "";
                }
            }

            return new EmailApiSettings
            {
                Provider = string.IsNullOrWhiteSpace(Leer("EmailApi:Provider")) ? "resend" : Leer("EmailApi:Provider"),
                ApiKey = Leer("EmailApi:ApiKey"),
                FromEmail = Leer("EmailApi:FromEmail"),
                FromName = string.IsNullOrWhiteSpace(Leer("EmailApi:FromName")) ? "Finanzas Personales" : Leer("EmailApi:FromName")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer configuracion de correo por API desde base de datos.");
            return new EmailApiSettings();
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
