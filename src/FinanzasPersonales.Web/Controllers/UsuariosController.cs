using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

// Solo el administrador gestiona usuarios.
public class UsuariosController : BaseController
{
    private readonly Db _db;
    private readonly EmailService _email;
    private readonly PreferenciasUsuarioService _preferencias;
    private readonly TraduccionService _traduccion;

    public UsuariosController(Db db, EmailService email, PreferenciasUsuarioService preferencias, TraduccionService traduccion)
    {
        _db = db;
        _email = email;
        _preferencias = preferencias;
        _traduccion = traduccion;
    }

    public IActionResult Index(bool incluirInactivos = false)
    {
        if (!EsAdmin) return Forbid();
        using var con = _db.Abrir();
        var todos = con.Query<Usuario>(
            @"SELECT id, nombre_usuario AS NombreUsuario, email, nombre_completo AS NombreCompleto,
                     es_admin AS EsAdmin, activo, email_confirmado AS EmailConfirmado,
                     intentos_fallidos AS IntentosFallidos, bloqueado_hasta AS BloqueadoHasta,
                     ultimo_acceso AS UltimoAcceso, creado_en AS CreadoEn,
                     valor_suscripcion AS ValorSuscripcion, ciclo_suscripcion AS CicloSuscripcion,
                     fecha_inicio_suscripcion AS FechaInicioSuscripcion, proximo_pago AS ProximoPago,
                     dias_gracia AS DiasGracia, estado_suscripcion AS EstadoSuscripcion,
                     notas_suscripcion AS NotasSuscripcion, suspendido_por_mora AS SuspendidoPorMora,
                     suspendido_en AS SuspendidoEn,
                     permiso_gastos AS PermisoGastos, permiso_prestamos AS PermisoPrestamos,
                     permiso_inversiones AS PermisoInversiones, permiso_directivo AS PermisoDirectivo,
                     permiso_asistente AS PermisoAsistente, permiso_calendario AS PermisoCalendario,
                     idioma, moneda_codigo AS MonedaCodigo, zona_horaria AS ZonaHoraria,
                     COALESCE((SELECT SUM(p.monto) FROM usuario_pagos_suscripcion p WHERE p.usuario_id=usuarios.id),0) AS TotalPagadoSuscripcion,
                     (SELECT MAX(p.fecha_pago) FROM usuario_pagos_suscripcion p WHERE p.usuario_id=usuarios.id) AS UltimoPagoSuscripcion
              FROM usuarios ORDER BY activo DESC, nombre_completo").ToList();
        var usuarios = incluirInactivos
            ? todos
            : todos.Where(x => x.Activo).ToList();
        var idsVisibles = usuarios.Select(x => x.Id).ToHashSet();
        var pagos = con.Query<PagoSuscripcion>(
            @"SELECT id, usuario_id AS UsuarioId, fecha_pago AS FechaPago, monto,
                     COALESCE(moneda_codigo,'COP') AS MonedaCodigo,
                     periodo_cubierto AS PeriodoCubierto, metodo, referencia, notas, creado_en AS CreadoEn
              FROM usuario_pagos_suscripcion
              ORDER BY fecha_pago DESC, id DESC").Where(x => idsVisibles.Contains(x.UsuarioId)).ToList();
        return View(new UsuariosAdminVm
        {
            Usuarios = usuarios,
            Pagos = pagos,
            Monedas = _preferencias.Monedas(),
            Idiomas = _preferencias.Idiomas(),
            IncluirInactivos = incluirInactivos,
            InactivosOcultos = todos.Count(x => !x.Activo),
            TotalClientes = todos.Count
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Guardar(
        int id,
        string email,
        string nombreCompleto,
        string? clave,
        decimal valorSuscripcion = 0,
        string cicloSuscripcion = "mensual",
        DateTime? fechaInicioSuscripcion = null,
        DateTime? proximoPago = null,
        int diasGracia = 3,
        string estadoSuscripcion = "activa",
        string? notasSuscripcion = null,
        bool permisoGastos = false,
        bool permisoPrestamos = false,
        bool permisoInversiones = false,
        bool permisoDirectivo = false,
        bool permisoAsistente = false,
        bool permisoCalendario = false,
        string idioma = "es",
        string monedaCodigo = "COP",
        string? zonaHoraria = "America/Bogota",
        bool esAdmin = false,
        bool activo = true)
    {
        if (!EsAdmin) return Forbid();
        email = NormalizarEmail(email);
        nombreCompleto = (nombreCompleto ?? "").Trim();
        cicloSuscripcion = NormalizarCiclo(cicloSuscripcion);
        estadoSuscripcion = NormalizarEstado(estadoSuscripcion);
        idioma = PreferenciasUsuarioService.NormalizarIdioma(idioma);
        monedaCodigo = PreferenciasUsuarioService.NormalizarMoneda(monedaCodigo);
        zonaHoraria = string.IsNullOrWhiteSpace(zonaHoraria) ? "America/Bogota" : zonaHoraria.Trim();
        diasGracia = Math.Clamp(diasGracia, 0, 90);
        valorSuscripcion = Math.Max(0, valorSuscripcion);
        if (!EmailValido(email) || string.IsNullOrWhiteSpace(nombreCompleto))
        {
            TempData["Error"] = "Correo valido y nombre completo son obligatorios.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        var existe = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM usuarios WHERE LOWER(email)=LOWER(@email) AND id<>@id",
            new { email, id }) > 0;
        if (existe)
        {
            TempData["Error"] = $"Ya existe un usuario con el correo '{email}'.";
            return RedirectToAction("Index");
        }

        if (id == 0)
        {
            var errorClave = ValidarClaveFuerte(clave);
            if (errorClave != null)
            {
                TempData["Error"] = errorClave;
                return RedirectToAction("Index");
            }
            var nuevoId = con.ExecuteScalar<int>(
                @"INSERT INTO usuarios (nombre_usuario, email, nombre_completo, clave_hash, es_admin, activo, email_confirmado,
                         valor_suscripcion, ciclo_suscripcion, fecha_inicio_suscripcion, proximo_pago,
                         dias_gracia, estado_suscripcion, notas_suscripcion,
                         permiso_gastos, permiso_prestamos, permiso_inversiones, permiso_directivo, permiso_asistente, permiso_calendario,
                         idioma, moneda_codigo, zona_horaria)
                  VALUES (@nombreUsuario, @email, @nombreCompleto, @hash, @esAdmin, @activo, FALSE,
                         @valorSuscripcion, @cicloSuscripcion, @fechaInicioSuscripcion, @proximoPago,
                         @diasGracia, @estadoSuscripcion, @notasSuscripcion,
                         @permisoGastos, @permisoPrestamos, @permisoInversiones, @permisoDirectivo, @permisoAsistente, @permisoCalendario,
                         @idioma, @monedaCodigo, @zonaHoraria) RETURNING id",
                new
                {
                    nombreUsuario = "tmp_" + Guid.NewGuid().ToString("N")[..16],
                    email,
                    nombreCompleto,
                    hash = BCrypt.Net.BCrypt.HashPassword(clave),
                    esAdmin,
                    activo,
                    valorSuscripcion,
                    cicloSuscripcion,
                    fechaInicioSuscripcion,
                    proximoPago,
                    diasGracia,
                    estadoSuscripcion,
                    notasSuscripcion,
                    permisoGastos,
                    permisoPrestamos,
                    permisoInversiones,
                    permisoDirectivo,
                    permisoAsistente,
                    permisoCalendario,
                    idioma,
                    monedaCodigo,
                    zonaHoraria
                });
            con.Execute("UPDATE usuarios SET nombre_usuario=@nombreUsuario WHERE id=@nuevoId",
                new { nombreUsuario = $"u{nuevoId}", nuevoId });
            var envio = await EnviarConfirmacion(con, nuevoId, email, nombreCompleto);
            TempData[envio.Ok ? "Ok" : "Error"] = envio.Ok
                ? $"Usuario '{email}' creado. {envio.Message}"
                : $"Usuario '{email}' creado, pero no se pudo enviar la verificacion. {envio.Message}";
        }
        else
        {
            var actual = con.QueryFirstOrDefault<Usuario>(
                "SELECT id, email, email_confirmado AS EmailConfirmado FROM usuarios WHERE id=@id", new { id });
            if (actual == null)
            {
                TempData["Error"] = "Usuario no encontrado.";
                return RedirectToAction("Index");
            }

            var cambioEmail = !actual.Email.Equals(email, StringComparison.OrdinalIgnoreCase);
            con.Execute(
                @"UPDATE usuarios SET nombre_usuario=@nombreUsuario, email=@email, nombre_completo=@nombreCompleto,
                         es_admin=@esAdmin, activo=@activo,
                         valor_suscripcion=@valorSuscripcion, ciclo_suscripcion=@cicloSuscripcion,
                         fecha_inicio_suscripcion=@fechaInicioSuscripcion, proximo_pago=@proximoPago,
                         dias_gracia=@diasGracia, estado_suscripcion=@estadoSuscripcion,
                         notas_suscripcion=@notasSuscripcion,
                         permiso_gastos=@permisoGastos, permiso_prestamos=@permisoPrestamos,
                         permiso_inversiones=@permisoInversiones, permiso_directivo=@permisoDirectivo,
                         permiso_asistente=@permisoAsistente, permiso_calendario=@permisoCalendario,
                         idioma=@idioma, moneda_codigo=@monedaCodigo, zona_horaria=@zonaHoraria,
                         suspendido_por_mora=CASE WHEN @activo THEN FALSE ELSE suspendido_por_mora END,
                         suspendido_en=CASE WHEN @activo THEN NULL ELSE suspendido_en END,
                         email_confirmado=CASE WHEN @cambioEmail THEN FALSE ELSE email_confirmado END
                  WHERE id=@id",
                new
                {
                    id,
                    nombreUsuario = $"u{id}",
                    email,
                    nombreCompleto,
                    esAdmin,
                    activo,
                    cambioEmail,
                    valorSuscripcion,
                    cicloSuscripcion,
                    fechaInicioSuscripcion,
                    proximoPago,
                    diasGracia,
                    estadoSuscripcion,
                    notasSuscripcion,
                    permisoGastos,
                    permisoPrestamos,
                    permisoInversiones,
                    permisoDirectivo,
                    permisoAsistente,
                    permisoCalendario,
                    idioma,
                    monedaCodigo,
                    zonaHoraria
                });
            if (!string.IsNullOrWhiteSpace(clave))
            {
                var errorClave = ValidarClaveFuerte(clave);
                if (errorClave != null)
                {
                    TempData["Error"] = errorClave;
                    return RedirectToAction("Index");
                }
                con.Execute("UPDATE usuarios SET clave_hash=@hash, intentos_fallidos=0, bloqueado_hasta=NULL WHERE id=@id",
                    new { id, hash = BCrypt.Net.BCrypt.HashPassword(clave) });
            }
            if (cambioEmail)
            {
                var envio = await EnviarConfirmacion(con, id, email, nombreCompleto);
                TempData[envio.Ok ? "Ok" : "Error"] = envio.Ok
                    ? "Usuario actualizado. Enviamos una nueva verificacion de correo."
                    : $"Usuario actualizado, pero no se pudo enviar la verificacion. {envio.Message}";
            }
            else
            {
                TempData["Ok"] = "Usuario actualizado.";
            }
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RegistrarPago(int usuarioId, DateTime fechaPago, decimal monto, string periodoCubierto, string? metodo, string? referencia, string? notas, bool actualizarProximoPago = true)
    {
        if (!EsAdmin) return Forbid();
        if (monto <= 0 || string.IsNullOrWhiteSpace(periodoCubierto))
        {
            TempData["Error"] = "El pago debe tener monto y periodo cubierto.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        var usuario = con.QueryFirstOrDefault<Usuario>(
            @"SELECT id, ciclo_suscripcion AS CicloSuscripcion, proximo_pago AS ProximoPago,
                     moneda_codigo AS MonedaCodigo
              FROM usuarios WHERE id=@usuarioId", new { usuarioId });
        if (usuario == null)
        {
            TempData["Error"] = "Usuario no encontrado.";
            return RedirectToAction("Index");
        }

        con.Execute(
            @"INSERT INTO usuario_pagos_suscripcion(usuario_id,fecha_pago,monto,moneda_codigo,periodo_cubierto,metodo,referencia,notas)
              VALUES(@usuarioId,@fechaPago,@monto,@monedaCodigo,@periodoCubierto,@metodo,@referencia,@notas)",
            new
            {
                usuarioId,
                fechaPago = fechaPago.Date,
                monto,
                monedaCodigo = PreferenciasUsuarioService.NormalizarMoneda(usuario.MonedaCodigo),
                periodoCubierto = periodoCubierto.Trim(),
                metodo = metodo?.Trim(),
                referencia = referencia?.Trim(),
                notas = notas?.Trim()
            });

        if (actualizarProximoPago)
        {
            var baseFecha = usuario.ProximoPago.HasValue && usuario.ProximoPago.Value.Date > fechaPago.Date
                ? usuario.ProximoPago.Value.Date
                : fechaPago.Date;
            var siguiente = SiguienteVencimiento(baseFecha, usuario.CicloSuscripcion);
            con.Execute(
                @"UPDATE usuarios SET proximo_pago=@siguiente, estado_suscripcion='activa',
                         activo=TRUE, suspendido_por_mora=FALSE, suspendido_en=NULL
                  WHERE id=@usuarioId",
                new { usuarioId, siguiente });
        }

        TempData["Ok"] = "Pago de suscripcion registrado.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult SuspenderPorMora(int id)
    {
        if (!EsAdmin) return Forbid();
        using var con = _db.Abrir();
        con.Execute(
            @"UPDATE usuarios SET activo=FALSE, estado_suscripcion='moroso',
                     suspendido_por_mora=TRUE, suspendido_en=NOW()
              WHERE id=@id",
            new { id });
        TempData["Ok"] = "Usuario suspendido por mora. No podra ingresar hasta que lo reactives o registres pago.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ReactivarCliente(int id)
    {
        if (!EsAdmin) return Forbid();
        using var con = _db.Abrir();
        con.Execute(
            @"UPDATE usuarios SET activo=TRUE, estado_suscripcion='activa',
                     suspendido_por_mora=FALSE, suspendido_en=NULL
              WHERE id=@id",
            new { id });
        TempData["Ok"] = "Cliente reactivado.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReenviarVerificacion(int id)
    {
        if (!EsAdmin) return Forbid();
        using var con = _db.Abrir();
        var u = con.QueryFirstOrDefault<Usuario>(
            @"SELECT id, email, nombre_completo AS NombreCompleto, activo, email_confirmado AS EmailConfirmado
              FROM usuarios WHERE id=@id", new { id });
        if (u == null)
        {
            TempData["Error"] = "Usuario no encontrado.";
            return RedirectToAction("Index");
        }
        if (u.EmailConfirmado)
        {
            TempData["Ok"] = "El correo ya esta verificado.";
            return RedirectToAction("Index");
        }

        var envio = await EnviarConfirmacion(con, u.Id, u.Email, u.NombreCompleto);
        TempData[envio.Ok ? "Ok" : "Error"] = envio.Message;
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        if (!EsAdmin) return Forbid();
        if (id == UsuarioId)
        {
            TempData["Error"] = "No puedes eliminar tu propio usuario administrador.";
            return RedirectToAction("Index", new { incluirInactivos = true });
        }

        using var con = _db.Abrir();
        var usuario = con.QueryFirstOrDefault<Usuario>(
            "SELECT id, email, nombre_completo AS NombreCompleto, es_admin AS EsAdmin FROM usuarios WHERE id=@id",
            new { id });
        if (usuario == null)
        {
            TempData["Error"] = "Usuario no encontrado.";
            return RedirectToAction("Index", new { incluirInactivos = true });
        }
        if (usuario.EsAdmin)
        {
            TempData["Error"] = "No se permite eliminar usuarios administradores desde esta accion.";
            return RedirectToAction("Index", new { incluirInactivos = true });
        }

        using var tx = con.BeginTransaction();
        try
        {
            var tablas = new[]
            {
                "usuario_tokens",
                "usuario_pagos_suscripcion",
                "configuraciones_usuario",
                "aportes_meta",
                "metas_ahorro",
                "presupuestos",
                "movimientos",
                "gastos_periodicos",
                "prestamo_pagos",
                "prestamos",
                "personas",
                "inversion_valoraciones",
                "inversion_movimientos",
                "inversiones",
                "tipos_inversion",
                "categorias",
                "cuentas"
            };
            foreach (var tabla in tablas)
            {
                con.Execute($"""
                    DO $$
                    BEGIN
                      IF to_regclass('public.{tabla}') IS NOT NULL THEN
                        DELETE FROM {tabla} WHERE usuario_id = {id};
                      END IF;
                    END $$;
                    """, transaction: tx);
            }
            con.Execute("DELETE FROM usuarios WHERE id=@id", new { id }, tx);
            tx.Commit();
            TempData["Ok"] = $"Usuario de prueba '{usuario.Email}' eliminado con sus datos asociados.";
        }
        catch (Exception ex)
        {
            tx.Rollback();
            TempData["Error"] = "No se pudo eliminar el usuario: " + ex.Message;
        }
        return RedirectToAction("Index", new { incluirInactivos = true });
    }

    private async Task<EmailEnvioResultado> EnviarConfirmacion(System.Data.IDbConnection con, int usuarioId, string email, string nombre)
    {
        if (!_email.Configurado)
            return new EmailEnvioResultado(false, "SMTP no esta configurado completamente. Revisa Integraciones y vuelve a reenviar la verificacion.");

        var token = CrearTokenSeguro();
        var idioma = con.ExecuteScalar<string?>("SELECT idioma FROM usuarios WHERE id=@usuarioId", new { usuarioId }) ?? "es";
        con.Execute(
            @"UPDATE usuario_tokens SET usado_en=NOW()
              WHERE usuario_id=@usuarioId AND tipo='confirmar_email' AND usado_en IS NULL;
              INSERT INTO usuario_tokens(usuario_id,tipo,token_hash,email_destino,expira_en)
              VALUES(@usuarioId,'confirmar_email',@tokenHash,@email,NOW() + INTERVAL '24 hours')",
            new { usuarioId, tokenHash = HashToken(token), email });
        var url = Url.Action("ConfirmarEmail", "Acceso", new { token }, Request.Scheme)!;
        try
        {
            await _email.EnviarAsync(email, _traduccion.T("Verifica tu correo", idioma) + " - Finanzas Personales",
                $"""
                <div style="font-family:Arial,sans-serif;max-width:620px;margin:auto;padding:24px;background:#F5F5F7;color:#1C1C1E">
                  <div style="background:#fff;border:1px solid #E5E7EB;border-radius:16px;padding:28px">
                    <div style="color:#7C3AED;font-weight:800;letter-spacing:.08em;text-transform:uppercase;font-size:12px">Finanzas Personales</div>
                    <h2 style="margin:8px 0 10px;color:#1C1C1E">{_traduccion.T("Verifica tu correo", idioma)}</h2>
                    <p>{_traduccion.T("Hola", idioma)} {System.Net.WebUtility.HtmlEncode(nombre)}, {_traduccion.T("Confirma que este correo te pertenece para activar el acceso seguro.", idioma)}</p>
                    <p style="margin:28px 0"><a href="{url}" style="background:#7C3AED;color:white;text-decoration:none;padding:12px 18px;border-radius:10px;font-weight:700">{_traduccion.T("Verificar correo", idioma)}</a></p>
                    <p style="font-size:13px;color:#6B7280">{_traduccion.T("Este enlace vence en 24 horas.", idioma)}</p>
                    <p style="font-size:12px;color:#6B7280;word-break:break-all">{_traduccion.T("Si el boton no funciona, copia este enlace:", idioma)}<br>{url}</p>
                  </div>
                </div>
                """);
            return new EmailEnvioResultado(true, "Enlace de verificacion enviado correctamente.");
        }
        catch (Exception ex)
        {
            return new EmailEnvioResultado(false, ex.Message);
        }
    }

    private sealed record EmailEnvioResultado(bool Ok, string Message);

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

    private static string NormalizarCiclo(string? ciclo) => (ciclo ?? "").Trim().ToLowerInvariant() switch
    {
        "trimestral" => "trimestral",
        "semestral" => "semestral",
        "anual" => "anual",
        _ => "mensual"
    };

    private static string NormalizarEstado(string? estado) => (estado ?? "").Trim().ToLowerInvariant() switch
    {
        "prueba" => "prueba",
        "moroso" => "moroso",
        "cancelado" => "cancelado",
        _ => "activa"
    };

    private static DateTime SiguienteVencimiento(DateTime desde, string ciclo) => NormalizarCiclo(ciclo) switch
    {
        "trimestral" => desde.AddMonths(3),
        "semestral" => desde.AddMonths(6),
        "anual" => desde.AddYears(1),
        _ => desde.AddMonths(1)
    };

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
}
