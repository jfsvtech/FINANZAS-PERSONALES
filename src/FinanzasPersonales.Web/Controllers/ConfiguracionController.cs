using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class ConfiguracionController : BaseController
{
    private readonly IConfiguration _config;
    private readonly EmailService _email;
    private readonly WhatsAppService _whatsApp;

    public ConfiguracionController(IConfiguration config, EmailService email, WhatsAppService whatsApp)
    {
        _config = config;
        _email = email;
        _whatsApp = whatsApp;
    }

    public IActionResult Integraciones()
    {
        if (!EsAdmin) return Forbid();
        var smtp = _email.ObtenerConfiguracion();
        var diagnosticoSmtp = _email.ObtenerDiagnostico();
        var whatsApp = _whatsApp.ObtenerConfiguracion();
        return View(new ConfiguracionIntegracionesVm
        {
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
            WhatsAppTokenGuardado = !string.IsNullOrWhiteSpace(whatsApp.AccessToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult GuardarSmtp(string host, int port, string user, string? password, string? from, bool enableSsl = false)
    {
        if (!EsAdmin) return Forbid();
        host = (host ?? "").Trim();
        user = (user ?? "").Trim();
        from = (from ?? "").Trim();
        password = (password ?? "").Trim();
        port = port <= 0 ? 587 : port;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
        {
            TempData["Error"] = "Servidor SMTP y usuario/correo son obligatorios.";
            return RedirectToAction("Integraciones");
        }

        var actual = _email.ObtenerConfiguracion();
        var actualizarPassword = !string.IsNullOrWhiteSpace(password);
        if (!actualizarPassword && string.IsNullOrWhiteSpace(actual.Password))
        {
            TempData["Error"] = "Debes ingresar la contrasena SMTP al configurar por primera vez.";
            return RedirectToAction("Integraciones");
        }

        _email.GuardarConfiguracion(new SmtpSettings
        {
            Host = host,
            Port = port,
            User = user,
            Password = actualizarPassword ? password! : actual.Password,
            From = string.IsNullOrWhiteSpace(from) ? user : from,
            EnableSsl = enableSsl
        }, actualizarPassword);

        TempData["Ok"] = "Configuracion SMTP guardada correctamente.";
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
                : "SMTP no esta configurado completamente.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = "No se pudo enviar la prueba SMTP: " + ex.Message;
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
}
