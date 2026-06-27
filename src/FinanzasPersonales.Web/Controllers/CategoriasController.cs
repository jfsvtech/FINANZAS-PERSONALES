using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class CategoriasController : BaseController
{
    private readonly Db _db;
    public CategoriasController(Db db) => _db = db;

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var categorias = con.Query<Categoria>(
            @"SELECT id, usuario_id AS UsuarioId, nombre, tipo, clase, color, icono, activo
              FROM categorias WHERE usuario_id=@UsuarioId
              ORDER BY activo DESC, tipo, nombre", new { UsuarioId }).ToList();
        return View(categorias);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string nombre, string tipo, string clase, string color, string? icono, bool activo = true)
    {
        nombre = (nombre ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nombre)
            || tipo is not ("ingreso" or "gasto")
            || clase is not ("fijo" or "variable" or "periodico"))
        {
            TempData["Error"] = "Datos incompletos.";
            return RedirectToAction("Index");
        }
        if (string.IsNullOrWhiteSpace(color)) color = "#6c757d";
        icono = ValidarIcono(icono);
        if (tipo == "ingreso") clase = "variable";

        using var con = _db.Abrir();
        var repetida = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM categorias
              WHERE usuario_id=@UsuarioId AND LOWER(nombre)=LOWER(@nombre) AND tipo=@tipo AND id<>@id",
            new { UsuarioId, nombre, tipo, id }) > 0;
        if (repetida)
        {
            TempData["Error"] = $"Ya tienes una categoria de {tipo} llamada '{nombre}'.";
            return RedirectToAction("Index");
        }

        if (id == 0)
        {
            con.Execute(@"INSERT INTO categorias (usuario_id, nombre, tipo, clase, color, icono, activo)
                          VALUES (@UsuarioId, @nombre, @tipo, @clase, @color, @icono, @activo)",
                new { UsuarioId, nombre, tipo, clase, color, icono, activo });
            TempData["Ok"] = "Categoria creada.";
        }
        else
        {
            var categoriaActual = con.QueryFirstOrDefault<Categoria>(
                "SELECT id, tipo FROM categorias WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId });
            if (categoriaActual == null) return NotFound();

            var enUso = con.ExecuteScalar<int>(
                @"SELECT (SELECT COUNT(*) FROM movimientos WHERE categoria_id=@id AND usuario_id=@UsuarioId)
                       + (SELECT COUNT(*) FROM presupuestos WHERE categoria_id=@id AND usuario_id=@UsuarioId)
                       + (SELECT COUNT(*) FROM gastos_periodicos WHERE categoria_id=@id AND usuario_id=@UsuarioId)",
                new { id, UsuarioId }) > 0;
            if (enUso && categoriaActual.Tipo != tipo)
            {
                TempData["Error"] = "No puedes cambiar entre ingreso y gasto porque la categoria ya tiene informacion asociada.";
                return RedirectToAction("Index");
            }

            con.Execute(@"UPDATE categorias SET nombre=@nombre, tipo=@tipo, clase=@clase, color=@color, icono=@icono, activo=@activo
                          WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, nombre, tipo, clase, color, icono, activo });
            TempData["Ok"] = "Categoria actualizada.";
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CrearSugeridas()
    {
        using var con = _db.Abrir();
        var existentes = con.Query<(string Nombre, string Tipo)>(
                "SELECT nombre, tipo FROM categorias WHERE usuario_id=@UsuarioId",
                new { UsuarioId })
            .Select(x => $"{x.Tipo}|{x.Nombre}".ToLowerInvariant())
            .ToHashSet();

        var sugeridas = new (string Nombre, string Tipo, string Clase, string Color)[]
        {
            ("Salario", "ingreso", "variable", "#2e7d32"),
            ("Otros ingresos", "ingreso", "variable", "#66bb6a"),
            ("Vivienda", "gasto", "fijo", "#c62828"),
            ("Servicios", "gasto", "fijo", "#e53935"),
            ("Mercado", "gasto", "variable", "#fb8c00"),
            ("Transporte", "gasto", "variable", "#ffb300"),
            ("Salud", "gasto", "variable", "#00897b"),
            ("Entretenimiento", "gasto", "variable", "#8e24aa"),
            ("Otros gastos", "gasto", "variable", "#6c757d")
        };

        foreach (var c in sugeridas.Where(c => !existentes.Contains($"{c.Tipo}|{c.Nombre}".ToLowerInvariant())))
        {
            con.Execute(
                @"INSERT INTO categorias (usuario_id, nombre, tipo, clase, color)
                  VALUES (@UsuarioId, @Nombre, @Tipo, @Clase, @Color)",
                new { UsuarioId, c.Nombre, c.Tipo, c.Clase, c.Color });
        }

        var creadas = sugeridas.Count(c => !existentes.Contains($"{c.Tipo}|{c.Nombre}".ToLowerInvariant()));
        TempData["Ok"] = creadas > 0
            ? $"Se agregaron {creadas} categorias sugeridas. Puedes editarlas o eliminarlas."
            : "Ya tienes todas las categorias sugeridas.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        var enUso = con.ExecuteScalar<int>(
            @"SELECT (SELECT COUNT(*) FROM movimientos WHERE categoria_id=@id AND usuario_id=@UsuarioId)
                   + (SELECT COUNT(*) FROM presupuestos WHERE categoria_id=@id AND usuario_id=@UsuarioId)
                   + (SELECT COUNT(*) FROM gastos_periodicos WHERE categoria_id=@id AND usuario_id=@UsuarioId)",
            new { id, UsuarioId }) > 0;

        if (enUso)
        {
            con.Execute("UPDATE categorias SET activo=FALSE WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
            TempData["Ok"] = "La categoria esta en uso: se marco como inactiva.";
        }
        else
        {
            con.Execute("DELETE FROM categorias WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
            TempData["Ok"] = "Categoria eliminada.";
        }
        return RedirectToAction("Index");
    }

    private static string ValidarIcono(string? icono) =>
        !string.IsNullOrWhiteSpace(icono) && icono.StartsWith("bi-", StringComparison.Ordinal) && icono.Length <= 50
            ? icono : "bi-tag";
}
