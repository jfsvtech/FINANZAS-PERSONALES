using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class PresupuestosController : BaseController
{
    private readonly Db _db;
    public PresupuestosController(Db db) => _db = db;

    public IActionResult Index()
    {
        var hoy = DateTime.Today;
        var desde = new DateTime(hoy.Year, hoy.Month, 1);
        var hasta = desde.AddMonths(1);

        using var con = _db.Abrir();
        var presupuestos = con.Query<Presupuesto>(
            @"SELECT p.id, p.usuario_id AS UsuarioId, p.categoria_id AS CategoriaId, p.monto_mensual AS MontoMensual,
                     cat.nombre AS CategoriaNombre, cat.color AS CategoriaColor, cat.icono AS CategoriaIcono,
                     COALESCE((SELECT SUM(m.monto) FROM movimientos m
                               WHERE m.usuario_id=@UsuarioId AND m.tipo='gasto'
                                 AND m.categoria_id=p.categoria_id AND m.fecha>=@desde AND m.fecha<@hasta),0) AS Gastado
              FROM presupuestos p JOIN categorias cat ON cat.id=p.categoria_id
              WHERE p.usuario_id=@UsuarioId ORDER BY cat.nombre",
            new { UsuarioId, desde, hasta }).ToList();

        ViewBag.CategoriasGasto = con.Query<Categoria>(
            @"SELECT id, nombre, color, icono FROM categorias
              WHERE usuario_id=@UsuarioId AND tipo='gasto' AND activo ORDER BY nombre",
            new { UsuarioId }).ToList();

        return View(presupuestos);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int categoriaId, decimal montoMensual)
    {
        if (montoMensual <= 0) { TempData["Error"] = "El monto debe ser mayor que cero."; return RedirectToAction("Index"); }

        using var con = _db.Abrir();
        var esMia = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM categorias WHERE id=@categoriaId AND usuario_id=@UsuarioId AND tipo='gasto'",
            new { categoriaId, UsuarioId }) > 0;
        if (!esMia) return Forbid();

        con.Execute(
            @"INSERT INTO presupuestos (usuario_id, categoria_id, monto_mensual)
              VALUES (@UsuarioId, @categoriaId, @montoMensual)
              ON CONFLICT (usuario_id, categoria_id) DO UPDATE SET monto_mensual=@montoMensual",
            new { UsuarioId, categoriaId, montoMensual });
        TempData["Ok"] = "Presupuesto guardado.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM presupuestos WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Presupuesto eliminado.";
        return RedirectToAction("Index");
    }
}
