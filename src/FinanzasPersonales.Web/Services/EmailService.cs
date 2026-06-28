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

    public bool Configurado => ObtenerConfiguracion().Configurado || ObtenerApiConfiguracion().Configurado;

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
        return new SmtpDiagnostico
        {
            TieneConfiguracionBaseDatos = false,
            PasswordCifradaEnBaseDatos = false,
            PasswordDescifrable = true,
            Mensaje = smtp.Configurado
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
        var settings = ObtenerConfiguracion();
        if (settings.Configurado)
            return await EnviarPorSmtpAsync(settings, destinatario, asunto, html);

        var api = ObtenerApiConfiguracion();
        if (api.Configurado)
            return await EnviarPorApiAsync(api, destinatario, asunto, html);

        _logger.LogWarning("Correo no configurado. No se envio el correo '{Subject}' a {Email}.", asunto, destinatario);
        return false;
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
