using Dapper;
using FinanzasPersonales.Web.Data;

namespace FinanzasPersonales.Web.Services;

public class AuditoriaService
{
    private readonly Db _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditoriaService> _logger;

    public AuditoriaService(Db db, IHttpContextAccessor http, ILogger<AuditoriaService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public void Registrar(int? usuarioId, int? actorUsuarioId, string modulo, string accion, string entidad, int? entidadId, string resumen)
    {
        try
        {
            var ctx = _http.HttpContext;
            using var con = _db.Abrir();
            con.Execute(
                @"INSERT INTO auditoria_eventos(usuario_id,actor_usuario_id,modulo,accion,entidad,entidad_id,resumen,ip,user_agent)
                  VALUES(@usuarioId,@actorUsuarioId,@modulo,@accion,@entidad,@entidadId,@resumen,@ip,@userAgent)",
                new
                {
                    usuarioId,
                    actorUsuarioId,
                    modulo = Limpiar(modulo, 80),
                    accion = Limpiar(accion, 80),
                    entidad = Limpiar(entidad, 80),
                    entidadId,
                    resumen = Limpiar(resumen, 500),
                    ip = Limpiar(ctx?.Connection.RemoteIpAddress?.ToString() ?? "", 80),
                    userAgent = Limpiar(ctx?.Request.Headers.UserAgent.ToString() ?? "", 300)
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo registrar auditoria {Modulo}/{Accion}.", modulo, accion);
        }
    }

    public void Login(int? usuarioId, string email, bool exitoso, string motivo)
    {
        try
        {
            var ctx = _http.HttpContext;
            using var con = _db.Abrir();
            con.Execute(
                @"INSERT INTO login_eventos(usuario_id,email,exitoso,motivo,ip,user_agent)
                  VALUES(@usuarioId,@email,@exitoso,@motivo,@ip,@userAgent)",
                new
                {
                    usuarioId,
                    email = Limpiar(email, 180),
                    exitoso,
                    motivo = Limpiar(motivo, 120),
                    ip = Limpiar(ctx?.Connection.RemoteIpAddress?.ToString() ?? "", 80),
                    userAgent = Limpiar(ctx?.Request.Headers.UserAgent.ToString() ?? "", 300)
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo registrar evento de login.");
        }
    }

    private static string Limpiar(string? valor, int max) =>
        string.IsNullOrWhiteSpace(valor) ? "" : valor.Trim()[..Math.Min(valor.Trim().Length, max)];
}
