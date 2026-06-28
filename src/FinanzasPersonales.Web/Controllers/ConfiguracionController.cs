using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class ConfiguracionController : BaseController
{
    private readonly IConfiguration _config;
    private readonly Db _db;
    private readonly EmailService _email;
    private readonly WhatsAppService _whatsApp;

    public ConfiguracionController(IConfiguration config, Db db, EmailService email, WhatsAppService whatsApp)
    {
        _config = config;
        _db = db;
        _email = email;
        _whatsApp = whatsApp;
    }

    public IActionResult Integraciones()
    {
        if (!EsAdmin) return Forbid();
        var smtp = _email.ObtenerConfiguracion();
        var emailApi = _email.ObtenerApiConfiguracion();
        var gmailApi = _email.ObtenerGmailApiConfiguracion();
        var diagnosticoSmtp = _email.ObtenerDiagnostico();
        var whatsApp = _whatsApp.ObtenerConfiguracion();
        var recordatorios = ObtenerConfiguracionRecordatoriosEmail();
        return View(new ConfiguracionIntegracionesVm
        {
            EmailProvider = (_config["Notifications:Email:Provider"] ?? _config["EMAIL_PROVIDER"] ?? "smtp").Trim().ToLowerInvariant(),
            GmailApiConfigurado = gmailApi.Configurado,
            GmailApiFromEmail = gmailApi.FromEmail,
            GmailApiFromName = gmailApi.FromName,
            EmailApiConfigurado = emailApi.Configurado,
            EmailApiProvider = emailApi.Provider,
            EmailApiFromEmail = emailApi.FromEmail,
            EmailApiFromName = emailApi.FromName,
            EmailApiKeyGuardada = !string.IsNullOrWhiteSpace(emailApi.ApiKey),
            SmtpConfigurado = smtp.Configurado,
            SmtpHost = smtp.Host,
            SmtpPort = smtp.Port,
            SmtpUser = smtp.User,
            SmtpFrom = smtp.From,
            SmtpEnableSsl = smtp.EnableSsl,
            SmtpPasswordGuardada = !string.IsNullOrWhiteSpace(smtp.Password),
            SmtpPasswordCifradaEnBaseDatos = diagnosticoSmtp.PasswordCifradaEnBaseDatos,
            SmtpPasswordDescifrable = diagnosticoSmtp.PasswordDescifrable,
            SmtpDiagnostico = diagnosticoSmtp.Mensaje,
            WhatsAppConfigurado = whatsApp.Configurado,
            WhatsAppPhoneNumberId = whatsApp.PhoneNumberId,
            WhatsAppGraphApiVersion = whatsApp.GraphApiVersion,
            WhatsAppAdminPhone = whatsApp.AdminPhone,
            WhatsAppTemplateName = whatsApp.TemplateName,
            WhatsAppTemplateLanguage = whatsApp.TemplateLanguage,
            WhatsAppTokenGuardado = !string.IsNullOrWhiteSpace(whatsApp.AccessToken),
            RecordatoriosEmailActivos = recordatorios.Activos,
            RecordatoriosEmailDiasAntes = recordatorios.DiasAntes
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarEmailApi(string provider, string? apiKey, string fromEmail, string? fromName)
    {
        if (!EsAdmin) return Forbid();
        TempData["Error"] = "La configuracion de correo se administra con variables de entorno en Railway, no desde la base de datos.";
        return RedirectToAction("Integraciones");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarSmtp(string host, int port, string user, string? password, string? from, bool enableSsl = false)
    {
        if (!EsAdmin) return Forbid();
        TempData["Error"] = "La configuracion SMTP se administra con variables de entorno en Railway, no desde la base de datos.";
        return RedirectToAction("Integraciones");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProbarSmtp(string emailPrueba)
    {
        if (!EsAdmin) return Forbid();
        emailPrueba = (emailPrueba ?? "").Trim();
        if (string.IsNullOrWhiteSpace(emailPrueba))
        {
            TempData["Error"] = "Ingresa un correo para enviar la prueba.";
            return RedirectToAction("Integraciones");
        }

        try
        {
            var enviado = await _email.EnviarPruebaAsync(emailPrueba);
            TempData[enviado ? "Ok" : "Error"] = enviado
                ? "Correo de prueba enviado correctamente."
                : "El correo no esta configurado completamente.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = "No se pudo enviar la prueba de correo: " + ex.Message;
        }
        return RedirectToAction("Integraciones");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarWhatsApp(string phoneNumberId, string? accessToken, string graphApiVersion, string? adminPhone, string? templateName, string? templateLanguage)
    {
        if (!EsAdmin) return Forbid();
        phoneNumberId = (phoneNumberId ?? "").Trim();
        accessToken = (accessToken ?? "").Trim();
        graphApiVersion = (graphApiVersion ?? "").Trim();
        adminPhone = (adminPhone ?? "").Trim();
        templateName = (templateName ?? "").Trim();
        templateLanguage = (templateLanguage ?? "").Trim();

        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            TempData["Error"] = "Phone Number ID de WhatsApp es obligatorio.";
            return RedirectToAction("Integraciones");
        }

        var actual = _whatsApp.ObtenerConfiguracion();
        var actualizarToken = !string.IsNullOrWhiteSpace(accessToken);
        if (!actualizarToken && string.IsNullOrWhiteSpace(actual.AccessToken))
        {
            TempData["Error"] = "Debes ingresar el token de acceso al configurar WhatsApp por primera vez.";
            return RedirectToAction("Integraciones");
        }

        _whatsApp.GuardarConfiguracion(new WhatsAppSettings
        {
            PhoneNumberId = phoneNumberId,
            AccessToken = actualizarToken ? accessToken! : actual.AccessToken,
            GraphApiVersion = string.IsNullOrWhiteSpace(graphApiVersion) ? "v23.0" : graphApiVersion,
            AdminPhone = adminPhone ?? "",
            TemplateName = templateName ?? "",
            TemplateLanguage = string.IsNullOrWhiteSpace(templateLanguage) ? "es" : templateLanguage!
        }, actualizarToken);

        TempData["Ok"] = "Configuracion de WhatsApp guardada correctamente.";
        return RedirectToAction("Integraciones");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarRecordatoriosEmail(bool activo = false, int diasAntes = 3)
    {
        if (!EsAdmin) return Forbid();

        diasAntes = Math.Clamp(diasAntes, 0, 60);
        using var con = _db.Abrir();
        GuardarConfiguracion(con, "RecordatoriosEmail:Activo", activo ? "true" : "false");
        GuardarConfiguracion(con, "RecordatoriosEmail:DiasAntes", diasAntes.ToString());

        TempData["Ok"] = activo
            ? $"Recordatorios automaticos activados. Se avisara {diasAntes} dia(s) antes."
            : "Recordatorios automaticos por correo desactivados.";
        return RedirectToAction("Integraciones");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProbarWhatsApp(string telefonoPrueba, bool usarPlantilla = false)
    {
        if (!EsAdmin) return Forbid();
        telefonoPrueba = WhatsAppService.NormalizarTelefono(telefonoPrueba);
        if (string.IsNullOrWhiteSpace(telefonoPrueba))
        {
            TempData["Error"] = "Ingresa un telefono valido para la prueba de WhatsApp.";
            return RedirectToAction("Integraciones");
        }

        var result = usarPlantilla
            ? await _whatsApp.EnviarPlantillaAsync(telefonoPrueba, "Prueba", "Finanzas Personales", DateTime.Today.ToString("dd/MM/yyyy"))
            : await _whatsApp.EnviarPruebaAsync(telefonoPrueba);
        TempData[result.Ok ? "Ok" : "Error"] = result.Message;
        return RedirectToAction("Integraciones");
    }

    private (bool Activos, int DiasAntes) ObtenerConfiguracionRecordatoriosEmail()
    {
        using var con = _db.Abrir();
        var filas = con.Query<(string Clave, string? Valor)>(
            @"SELECT clave, valor FROM configuraciones_sistema
              WHERE clave IN ('RecordatoriosEmail:Activo', 'RecordatoriosEmail:DiasAntes')")
            .ToDictionary(x => x.Clave, x => x.Valor, StringComparer.OrdinalIgnoreCase);

        var activos = !filas.TryGetValue("RecordatoriosEmail:Activo", out var activoRaw)
            || bool.TryParse(activoRaw, out var activo) && activo;
        var diasAntes = filas.TryGetValue("RecordatoriosEmail:DiasAntes", out var diasRaw)
            && int.TryParse(diasRaw, out var dias)
            ? Math.Clamp(dias, 0, 60)
            : 3;
        return (activos, diasAntes);
    }

    private static void GuardarConfiguracion(System.Data.IDbConnection con, string clave, string valor)
    {
        con.Execute(
            @"INSERT INTO configuraciones_sistema(clave, valor, protegido, actualizado_en)
              VALUES(@clave, @valor, FALSE, NOW())
              ON CONFLICT (clave) DO UPDATE
              SET valor=EXCLUDED.valor, protegido=FALSE, actualizado_en=NOW()",
            new { clave, valor });
    }
}
