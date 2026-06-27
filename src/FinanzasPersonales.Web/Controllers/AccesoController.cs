using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FinanzasPersonales.Web.Controllers;

public class AccesoController : Controller
{
    private const int MaxIntentosFallidos = 5;
    private static readonly TimeSpan BloqueoTemporal = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VigenciaRecuperacion = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan VigenciaConfirmacion = TimeSpan.FromHours(24);

    private readonly Db _db;
    private readonly EmailService _email;

    public AccesoController(Db db, EmailService email)
    {
        _db = db;
        _email = email;
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Inicio");
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [EnableRateLimiting("auth")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string clave)
    {
        email = NormalizarEmail(email);
        using var con = _db.Abrir();
        var u = con.QueryFirstOrDefault<Usuario>(
            @"SELECT id, nombre_usuario AS NombreUsuario, email, nombre_completo AS NombreCompleto,
                     clave_hash AS ClaveHash, es_admin AS EsAdmin, activo,
                     email_confirmado AS EmailConfirmado, intentos_fallidos AS IntentosFallidos,
                     bloqueado_hasta AS BloqueadoHasta,
                     permiso_gastos AS PermisoGastos, permiso_prestamos AS PermisoPrestamos,
                     permiso_inversiones AS PermisoInversiones, permiso_directivo AS PermisoDirectivo,
                     permiso_asistente AS PermisoAsistente, permiso_calendario AS PermisoCalendario
              FROM usuarios WHERE LOWER(email) = LOWER(@email)",
            new { email });

        if (u == null)
        {
            await Task.Delay(RandomNumberGenerator.GetInt32(180, 420));
            ViewBag.Error = "Correo o contrasena incorrectos.";
            return View();
        }

        if (u.BloqueadoHasta.HasValue && u.BloqueadoHasta.Value > DateTime.Now)
        {
            ViewBag.Error = $"Cuenta bloqueada temporalmente. Intenta de nuevo despues de las {u.BloqueadoHasta.Value:HH:mm}.";
            return View();
        }

        if (!u.Activo)
        {
            ViewBag.Error = "La cuenta esta inactiva. Contacta al administrador.";
            return View();
        }

        if (!u.EmailConfirmado)
        {
            ViewBag.Error = "Debes verificar tu correo electronico antes de ingresar.";
            return View();
        }

        if (!BCrypt.Net.BCrypt.Verify(clave ?? "", u.ClaveHash))
        {
            RegistrarFallo(con, u.Id, u.IntentosFallidos);
            ViewBag.Error = "Correo o contrasena incorrectos.";
            return View();
        }

        con.Execute(
            @"UPDATE usuarios SET intentos_fallidos=0, bloqueado_hasta=NULL, ultimo_acceso=NOW()
              WHERE id=@id", new { u.Id });

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, u.NombreCompleto),
            new(ClaimTypes.Email, u.Email),
            new("EsAdmin", u.EsAdmin ? "true" : "false"),
            new("PermisoGastos", u.EsAdmin || u.PermisoGastos ? "true" : "false"),
            new("PermisoPrestamos", u.EsAdmin || u.PermisoPrestamos ? "true" : "false"),
            new("PermisoInversiones", u.EsAdmin || u.PermisoInversiones ? "true" : "false"),
            new("PermisoDirectivo", u.EsAdmin || u.PermisoDirectivo ? "true" : "false"),
            new("PermisoAsistente", u.EsAdmin || u.PermisoAsistente ? "true" : "false"),
            new("PermisoCalendario", u.EsAdmin || u.PermisoCalendario ? "true" : "false")
        };
        var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(new ClaimsPrincipal(identidad),
            new AuthenticationProperties
            {
                IsPersistent = false,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        return RedirectToAction("Index", "Inicio");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult RecuperarClave()
    {
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [EnableRateLimiting("auth")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RecuperarClave(string email)
    {
        email = NormalizarEmail(email);
        if (!EmailValido(email))
        {
            TempData["Ok"] = "Si el correo esta registrado, enviaremos un enlace de recuperacion.";
            return RedirectToAction("Login");
        }

        using var con = _db.Abrir();
        var u = con.QueryFirstOrDefault<Usuario>(
            @"SELECT id, email, nombre_completo AS NombreCompleto, activo, email_confirmado AS EmailConfirmado
              FROM usuarios WHERE LOWER(email)=LOWER(@email)",
            new { email });

        if (u?.Activo == true && u.EmailConfirmado)
        {
            var token = CrearTokenSeguro();
            GuardarToken(con, u.Id, "recuperar_clave", token, u.Email, VigenciaRecuperacion);
            var url = Url.Action("RestablecerClave", "Acceso", new { token }, Request.Scheme)!;
            await _email.EnviarAsync(u.Email, "Recuperar contrasena - Finanzas Personales",
                PlantillaCorreo("Recuperar contrasena", u.NombreCompleto,
                    "Recibimos una solicitud para restablecer tu contrasena.",
                    "Restablecer contrasena", url,
                    "Este enlace vence en 30 minutos y solo puede usarse una vez."));
        }

        TempData["Ok"] = "Si el correo esta registrado, enviaremos un enlace de recuperacion.";
        return RedirectToAction("Login");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult RestablecerClave(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return RedirectToAction("Login");
        ViewBag.Token = token;
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [EnableRateLimiting("auth")]
    [ValidateAntiForgeryToken]
    public IActionResult RestablecerClave(string token, string claveNueva, string confirmarClave)
    {
        if (claveNueva != confirmarClave)
        {
            ViewBag.Token = token;
            ViewBag.Error = "Las contrasenas no coinciden.";
            return View();
        }

        var errorClave = ValidarClaveFuerte(claveNueva);
        if (errorClave != null)
        {
            ViewBag.Token = token;
            ViewBag.Error = errorClave;
            return View();
        }

        using var con = _db.Abrir();
        var hashToken = HashToken(token);
        var registro = con.QueryFirstOrDefault<(int Id, int UsuarioId)>(
            @"SELECT id AS Id, usuario_id AS UsuarioId FROM usuario_tokens
              WHERE token_hash=@hashToken AND tipo='recuperar_clave'
                AND usado_en IS NULL AND expira_en > NOW()",
            new { hashToken });
        if (registro.Id == 0)
        {
            ViewBag.Error = "El enlace no es valido o ya vencio.";
            return View();
        }

        con.Execute(
            @"UPDATE usuarios SET clave_hash=@hash, intentos_fallidos=0, bloqueado_hasta=NULL
              WHERE id=@usuarioId;
              UPDATE usuario_tokens SET usado_en=NOW() WHERE id=@tokenId;
              UPDATE usuario_tokens SET usado_en=NOW()
              WHERE usuario_id=@usuarioId AND tipo='recuperar_clave' AND usado_en IS NULL",
            new { hash = BCrypt.Net.BCrypt.HashPassword(claveNueva), usuarioId = registro.UsuarioId, tokenId = registro.Id });

        TempData["Ok"] = "Contrasena actualizada. Ya puedes ingresar con tu correo.";
        return RedirectToAction("Login");
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult ConfirmarEmail(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return RedirectToAction("Login");

        using var con = _db.Abrir();
        var hashToken = HashToken(token);
        var registro = con.QueryFirstOrDefault<(int Id, int UsuarioId)>(
            @"SELECT id AS Id, usuario_id AS UsuarioId FROM usuario_tokens
              WHERE token_hash=@hashToken AND tipo='confirmar_email'
                AND usado_en IS NULL AND expira_en > NOW()",
            new { hashToken });
        if (registro.Id == 0)
        {
            TempData["Error"] = "El enlace de verificacion no es valido o ya vencio.";
            return RedirectToAction("Login");
        }

        con.Execute(
            @"UPDATE usuarios SET email_confirmado=TRUE WHERE id=@usuarioId;
              UPDATE usuario_tokens SET usado_en=NOW() WHERE id=@tokenId",
            new { usuarioId = registro.UsuarioId, tokenId = registro.Id });
        TempData["Ok"] = "Correo verificado correctamente. Ya puedes ingresar.";
        return RedirectToAction("Login");
    }

    [Authorize]
    public async Task<IActionResult> Salir()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction("Login");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CambiarClave(string claveActual, string claveNueva, string confirmarClave)
    {
        var usuarioId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (claveNueva != confirmarClave)
        {
            TempData["Error"] = "Las contrasenas no coinciden.";
            return RedirectToAction("Index", "Inicio");
        }
        var errorClave = ValidarClaveFuerte(claveNueva);
        if (errorClave != null)
        {
            TempData["Error"] = errorClave;
            return RedirectToAction("Index", "Inicio");
        }

        using var con = _db.Abrir();
        var hashActual = con.ExecuteScalar<string>(
            "SELECT clave_hash FROM usuarios WHERE id = @usuarioId", new { usuarioId });

        if (hashActual == null || !BCrypt.Net.BCrypt.Verify(claveActual ?? "", hashActual))
        {
            TempData["Error"] = "La contrasena actual no es correcta.";
            return RedirectToAction("Index", "Inicio");
        }

        con.Execute("UPDATE usuarios SET clave_hash = @hash WHERE id = @usuarioId",
            new { hash = BCrypt.Net.BCrypt.HashPassword(claveNueva), usuarioId });
        await HttpContext.SignOutAsync();
        TempData["Ok"] = "Contrasena actualizada. Ingresa de nuevo por seguridad.";
        return RedirectToAction("Login");
    }

    public async Task EnviarConfirmacionEmailAsync(int usuarioId, string email, string nombre)
    {
        using var con = _db.Abrir();
        var token = CrearTokenSeguro();
        GuardarToken(con, usuarioId, "confirmar_email", token, email, VigenciaConfirmacion);
        var url = Url.Action("ConfirmarEmail", "Acceso", new { token }, Request.Scheme)!;
        await _email.EnviarAsync(email, "Verifica tu correo - Finanzas Personales",
            PlantillaCorreo("Verifica tu correo", nombre,
                "Confirma que este correo te pertenece para activar el acceso seguro.",
                "Verificar correo", url,
                "Este enlace vence en 24 horas."));
    }

    private static void RegistrarFallo(System.Data.IDbConnection con, int usuarioId, int intentosActuales)
    {
        var nuevosIntentos = intentosActuales + 1;
        DateTime? bloqueo = nuevosIntentos >= MaxIntentosFallidos ? DateTime.Now.Add(BloqueoTemporal) : null;
        con.Execute(
            @"UPDATE usuarios SET intentos_fallidos=@nuevosIntentos, bloqueado_hasta=@bloqueo
              WHERE id=@usuarioId",
            new { nuevosIntentos, bloqueo, usuarioId });
    }

    private static string? ValidarClaveFuerte(string? clave)
    {
        if (string.IsNullOrWhiteSpace(clave) || clave.Length < 12)
            return "La contrasena debe tener al menos 12 caracteres.";
        if (!clave.Any(char.IsUpper) || !clave.Any(char.IsLower) || !clave.Any(char.IsDigit) || !clave.Any(c => !char.IsLetterOrDigit(c)))
            return "La contrasena debe incluir mayuscula, minuscula, numero y simbolo.";
        return null;
    }

    private static bool EmailValido(string email)
    {
        try
        {
            var address = new MailAddress(email);
            return address.Address.Equals(email, StringComparison.OrdinalIgnoreCase) &&
                   email.Contains('.') &&
                   !email.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizarEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static string CrearTokenSeguro()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private static void GuardarToken(System.Data.IDbConnection con, int usuarioId, string tipo, string token, string email, TimeSpan vigencia)
    {
        con.Execute(
            @"UPDATE usuario_tokens SET usado_en=NOW()
              WHERE usuario_id=@usuarioId AND tipo=@tipo AND usado_en IS NULL;
              INSERT INTO usuario_tokens(usuario_id,tipo,token_hash,email_destino,expira_en,creado_ip)
              VALUES(@usuarioId,@tipo,@tokenHash,@email,NOW() + (@segundos || ' seconds')::interval,@ip)",
            new
            {
                usuarioId,
                tipo,
                tokenHash = HashToken(token),
                email,
                segundos = (int)vigencia.TotalSeconds,
                ip = ""
            });
    }

    private static string PlantillaCorreo(string titulo, string nombre, string intro, string boton, string url, string pie)
    {
        return $"""
        <div style="font-family:Arial,sans-serif;max-width:620px;margin:auto;padding:24px;background:#F5F5F7;color:#1C1C1E">
          <div style="background:#fff;border:1px solid #E5E7EB;border-radius:16px;padding:28px">
            <div style="color:#7C3AED;font-weight:800;letter-spacing:.08em;text-transform:uppercase;font-size:12px">Finanzas Personales</div>
            <h2 style="margin:8px 0 10px;color:#1C1C1E">{titulo}</h2>
            <p>Hola {WebUtility.HtmlEncode(nombre)},</p>
            <p>{intro}</p>
            <p style="margin:28px 0"><a href="{url}" style="background:#7C3AED;color:white;text-decoration:none;padding:12px 18px;border-radius:10px;font-weight:700">{boton}</a></p>
            <p style="font-size:13px;color:#6B7280">{pie}</p>
            <p style="font-size:12px;color:#6B7280;word-break:break-all">Si el boton no funciona, copia este enlace:<br>{url}</p>
          </div>
        </div>
        """;
    }
}
