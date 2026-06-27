using System.Net;
using System.Net.Mail;
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
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, Db db, IDataProtectionProvider dataProtection, ILogger<EmailService> logger)
    {
        _config = config;
        _db = db;
        _protector = dataProtection.CreateProtector("FinanzasPersonales.SmtpSettings");
        _logger = logger;
    }

    public bool Configurado => ObtenerConfiguracion().Configurado;

    public SmtpSettings ObtenerConfiguracion()
    {
        var dbSettings = ObtenerDesdeBaseDatos();
        if (dbSettings.Configurado || !string.IsNullOrWhiteSpace(dbSettings.Host) || !string.IsNullOrWhiteSpace(dbSettings.User))
            return dbSettings;

        return new SmtpSettings
        {
            Host = _config["Notifications:Smtp:Host"] ?? "",
            Port = int.TryParse(_config["Notifications:Smtp:Port"], out var port) ? port : 587,
            User = _config["Notifications:Smtp:User"] ?? "",
            Password = _config["Notifications:Smtp:Password"] ?? "",
            From = _config["Notifications:Smtp:From"] ?? "",
            EnableSsl = !bool.TryParse(_config["Notifications:Smtp:EnableSsl"], out var ssl) || ssl
        };
    }

    public void GuardarConfiguracion(SmtpSettings settings, bool actualizarPassword)
    {
        using var con = _db.Abrir();
        Guardar(con, "Smtp:Host", settings.Host, false);
        Guardar(con, "Smtp:Port", settings.Port.ToString(), false);
        Guardar(con, "Smtp:User", settings.User, false);
        Guardar(con, "Smtp:From", settings.From, false);
        Guardar(con, "Smtp:EnableSsl", settings.EnableSsl ? "true" : "false", false);
        if (actualizarPassword)
            Guardar(con, "Smtp:Password", _protector.Protect(settings.Password), true);
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
            Timeout = 30000
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

    private static string CrearMensajeDiagnosticoSmtp(SmtpSettings settings, Exception ex)
    {
        var detalle = ex.InnerException?.Message ?? ex.Message;
        var proveedor = settings.Host.Contains("gmail", StringComparison.OrdinalIgnoreCase)
            ? " Para Gmail debes usar una contrasena de aplicacion de Google, tener la verificacion en 2 pasos activa y usar smtp.gmail.com con puerto 587 y SSL/TLS activo."
            : "";

        return $"No se pudo conectar o autenticar con el servidor SMTP ({settings.Host}:{settings.Port}). Detalle: {detalle}.{proveedor}";
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
                catch { return ""; }
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
