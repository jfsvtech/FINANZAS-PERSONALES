using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class TiposInversionController : BaseController
{
    private readonly Db _db;
    public TiposInversionController(Db db) => _db = db;

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var tipos = con.Query<TipoInversion>(
            @"SELECT t.id,t.usuario_id AS UsuarioId,t.nombre,t.color,t.icono,t.activo,
              (SELECT COUNT(*) FROM inversiones i WHERE i.tipo_inversion_id=t.id AND i.usuario_id=@UsuarioId) AS CantidadInversiones
              FROM tipos_inversion t WHERE t.usuario_id=@UsuarioId
              ORDER BY t.activo DESC,t.nombre", new { UsuarioId }).ToList();
        return View(tipos);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string nombre, string? color, string? icono, bool activo = true)
    {
        nombre = (nombre ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            TempData["Error"] = "El nombre es obligatorio.";
            return RedirectToAction("Index");
        }
        color = ValidarColor(color);
        icono = ValidarIcono(icono);
        using var con = _db.Abrir();
        var repetido = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM tipos_inversion
              WHERE usuario_id=@UsuarioId AND LOWER(nombre)=LOWER(@nombre) AND id<>@id",
            new { UsuarioId, nombre, id }) > 0;
        if (repetido)
        {
            TempData["Error"] = "Ya tienes un tipo de inversion con ese nombre.";
            return RedirectToAction("Index");
        }
        if (id == 0)
        {
            con.Execute(
                @"INSERT INTO tipos_inversion(usuario_id,nombre,color,icono,activo)
                  VALUES(@UsuarioId,@nombre,@color,@icono,@activo)",
                new { UsuarioId, nombre, color, icono, activo });
            TempData["Ok"] = "Tipo de inversion creado.";
        }
        else
        {
            con.Execute(
                @"UPDATE tipos_inversion SET nombre=@nombre,color=@color,icono=@icono,activo=@activo
                  WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, nombre, color, icono, activo });
            TempData["Ok"] = "Tipo de inversion actualizado.";
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        var enUso = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM inversiones WHERE tipo_inversion_id=@id AND usuario_id=@UsuarioId",
            new { id, UsuarioId }) > 0;
        if (enUso)
        {
            con.Execute("UPDATE tipos_inversion SET activo=FALSE WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId });
            TempData["Ok"] = "El tipo esta en uso: se marco como inactivo para conservar el historial.";
        }
        else
        {
            con.Execute("DELETE FROM tipos_inversion WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId });
            TempData["Ok"] = "Tipo de inversion eliminado.";
        }
        return RedirectToAction("Index");
    }

    private static string ValidarColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")
            ? color : "#D4AF37";

    private static string ValidarIcono(string? icono) =>
        !string.IsNullOrWhiteSpace(icono) && icono.StartsWith("bi-", StringComparison.Ordinal) && icono.Length <= 50
            ? icono : "bi-graph-up-arrow";
}
