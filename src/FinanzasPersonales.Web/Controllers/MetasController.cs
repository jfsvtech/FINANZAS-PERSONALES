using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class MetasController : BaseController
{
    private readonly Db _db;
    public MetasController(Db db) => _db = db;

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var metas = con.Query<MetaAhorro>(
            @"SELECT m.id, m.usuario_id AS UsuarioId, m.nombre, m.monto_objetivo AS MontoObjetivo,
                     m.fecha_objetivo AS FechaObjetivo, m.color, m.activo,
                     COALESCE((SELECT SUM(a.monto) FROM aportes_meta a
                               WHERE a.meta_ahorro_id=m.id AND a.usuario_id=@UsuarioId),0) AS Ahorrado
              FROM metas_ahorro m WHERE m.usuario_id=@UsuarioId
              ORDER BY m.activo DESC, m.fecha_objetivo NULLS LAST, m.nombre",
            new { UsuarioId }).ToList();
        return View(new MetasIndexVm { Metas = metas });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string nombre, decimal montoObjetivo, DateTime? fechaObjetivo, string color, bool activo = true)
    {
        nombre = (nombre ?? "").Trim();
        if (nombre.Length == 0 || montoObjetivo <= 0)
        {
            TempData["Error"] = "Nombre y monto objetivo son obligatorios.";
            return RedirectToAction("Index");
        }
        if (string.IsNullOrWhiteSpace(color)) color = "#6f42c1";
        using var con = _db.Abrir();
        if (id == 0)
            con.Execute(@"INSERT INTO metas_ahorro (usuario_id,nombre,monto_objetivo,fecha_objetivo,color,activo)
                          VALUES (@UsuarioId,@nombre,@montoObjetivo,@fechaObjetivo,@color,@activo)",
                new { UsuarioId, nombre, montoObjetivo, fechaObjetivo, color, activo });
        else
            con.Execute(@"UPDATE metas_ahorro SET nombre=@nombre,monto_objetivo=@montoObjetivo,
                          fecha_objetivo=@fechaObjetivo,color=@color,activo=@activo
                          WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, nombre, montoObjetivo, fechaObjetivo, color, activo });
        TempData["Ok"] = id == 0 ? "Meta creada." : "Meta actualizada.";
        return RedirectToAction("Index");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Aportar(int metaId, DateTime fecha, decimal monto, string? notas)
    {
        if (monto <= 0) return BadRequest();
        using var con = _db.Abrir();
        var esMia = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM metas_ahorro WHERE id=@metaId AND usuario_id=@UsuarioId",
            new { metaId, UsuarioId }) > 0;
        if (!esMia) return Forbid();
        con.Execute(@"INSERT INTO aportes_meta (usuario_id,meta_ahorro_id,fecha,monto,notas)
                      VALUES (@UsuarioId,@metaId,@fecha,@monto,@notas)",
            new { UsuarioId, metaId, fecha, monto, notas });
        TempData["Ok"] = "Aporte registrado.";
        return RedirectToAction("Index");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM aportes_meta WHERE meta_ahorro_id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        con.Execute("DELETE FROM metas_ahorro WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Meta eliminada.";
        return RedirectToAction("Index");
    }
}
