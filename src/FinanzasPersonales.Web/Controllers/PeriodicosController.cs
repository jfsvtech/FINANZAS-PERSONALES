using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class PeriodicosController : BaseController
{
    private readonly Db _db;
    public PeriodicosController(Db db) => _db = db;

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var lista = con.Query<GastoPeriodico>(
            @"SELECT g.id, g.usuario_id AS UsuarioId, g.tipo, g.nombre, g.categoria_id AS CategoriaId,
                     g.cuenta_id AS CuentaId, g.monto_estimado AS MontoEstimado,
                     g.frecuencia_meses AS FrecuenciaMeses, g.proxima_fecha AS ProximaFecha, g.activo,
                     cat.nombre AS CategoriaNombre, cat.icono AS CategoriaIcono, cu.nombre AS CuentaNombre
              FROM gastos_periodicos g
              JOIN categorias cat ON cat.id=g.categoria_id
              LEFT JOIN cuentas cu ON cu.id=g.cuenta_id
              WHERE g.usuario_id=@UsuarioId
              ORDER BY g.activo DESC, g.proxima_fecha", new { UsuarioId }).ToList();

        ViewBag.CategoriasGasto = con.Query<Categoria>(
            @"SELECT id, nombre, icono FROM categorias WHERE usuario_id=@UsuarioId AND tipo='gasto' AND activo ORDER BY nombre",
            new { UsuarioId }).ToList();
        ViewBag.CategoriasIngreso = con.Query<Categoria>(
            @"SELECT id, nombre, icono FROM categorias WHERE usuario_id=@UsuarioId AND tipo='ingreso' AND activo ORDER BY nombre",
            new { UsuarioId }).ToList();
        ViewBag.Cuentas = con.Query<Cuenta>(
            @"SELECT id, nombre, tipo FROM cuentas WHERE usuario_id=@UsuarioId AND activo ORDER BY tipo, nombre",
            new { UsuarioId }).ToList();

        return View(lista);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string tipo, string nombre, int categoriaId, int? cuentaId,
        decimal montoEstimado, int frecuenciaMeses, DateTime proximaFecha, bool activo = true)
    {
        tipo = NormalizarTipo(tipo);
        if (string.IsNullOrWhiteSpace(nombre) || montoEstimado <= 0 || frecuenciaMeses < 1)
        {
            TempData["Error"] = "Datos incompletos.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        var categoriaMia = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM categorias WHERE id=@categoriaId AND usuario_id=@UsuarioId AND tipo=@tipo",
            new { categoriaId, UsuarioId, tipo }) > 0;
        if (!categoriaMia) return Forbid();
        if (cuentaId.HasValue)
        {
            var cuentaMia = con.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM cuentas WHERE id=@cuentaId AND usuario_id=@UsuarioId",
                new { cuentaId, UsuarioId }) > 0;
            if (!cuentaMia) return Forbid();
        }

        if (id == 0)
        {
            con.Execute(
                @"INSERT INTO gastos_periodicos (usuario_id, tipo, nombre, categoria_id, cuenta_id, monto_estimado, frecuencia_meses, proxima_fecha, activo)
                  VALUES (@UsuarioId, @tipo, @nombre, @categoriaId, @cuentaId, @montoEstimado, @frecuenciaMeses, @proximaFecha, @activo)",
                new { UsuarioId, tipo, nombre = nombre.Trim(), categoriaId, cuentaId, montoEstimado, frecuenciaMeses, proximaFecha, activo });
            TempData["Ok"] = tipo == "ingreso" ? "Ingreso recurrente creado." : "Gasto periodico creado.";
        }
        else
        {
            con.Execute(
                @"UPDATE gastos_periodicos SET tipo=@tipo, nombre=@nombre, categoria_id=@categoriaId, cuenta_id=@cuentaId,
                         monto_estimado=@montoEstimado, frecuencia_meses=@frecuenciaMeses,
                         proxima_fecha=@proximaFecha, activo=@activo
                  WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, tipo, nombre = nombre.Trim(), categoriaId, cuentaId, montoEstimado, frecuenciaMeses, proximaFecha, activo });
            TempData["Ok"] = tipo == "ingreso" ? "Ingreso recurrente actualizado." : "Gasto periodico actualizado.";
        }
        return RedirectToAction("Index");
    }

    // Registra el movimiento real recurrente y corre la proxima fecha.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Registrar(int id, DateTime fecha, decimal monto, int cuentaId)
    {
        if (monto <= 0) { TempData["Error"] = "El monto debe ser mayor que cero."; return RedirectToAction("Index"); }

        using var con = _db.Abrir();
        var g = con.QueryFirstOrDefault<GastoPeriodico>(
            @"SELECT id, usuario_id AS UsuarioId, tipo, nombre, categoria_id AS CategoriaId,
                     frecuencia_meses AS FrecuenciaMeses, proxima_fecha AS ProximaFecha
              FROM gastos_periodicos WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        if (g == null) return NotFound();

        var cuentaMia = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM cuentas WHERE id=@cuentaId AND usuario_id=@UsuarioId",
            new { cuentaId, UsuarioId }) > 0;
        if (!cuentaMia) return Forbid();

        con.Execute(
            @"INSERT INTO movimientos (usuario_id, fecha, tipo, cuenta_id, categoria_id, descripcion, monto, gasto_periodico_id)
              VALUES (@UsuarioId, @fecha, @tipo, @cuentaId, @categoriaId, @descripcion, @monto, @id)",
            new { UsuarioId, fecha, tipo = g.Tipo, cuentaId, categoriaId = g.CategoriaId, descripcion = g.Nombre, monto, id });

        con.Execute(
            @"UPDATE gastos_periodicos SET proxima_fecha = proxima_fecha + (frecuencia_meses || ' months')::interval
              WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });

        TempData["Ok"] = $"{(g.Tipo == "ingreso" ? "Ingreso" : "Gasto")} '{g.Nombre}' registrado y reprogramado.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("UPDATE movimientos SET gasto_periodico_id=NULL WHERE gasto_periodico_id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        con.Execute("DELETE FROM gastos_periodicos WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Movimiento recurrente eliminado (los movimientos ya registrados se conservan).";
        return RedirectToAction("Index");
    }

    private static string NormalizarTipo(string? tipo) => (tipo ?? "").Trim().ToLowerInvariant() == "ingreso"
        ? "ingreso"
        : "gasto";
}
