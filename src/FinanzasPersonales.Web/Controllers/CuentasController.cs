using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class CuentasController : BaseController
{
    private readonly Db _db;
    public CuentasController(Db db) => _db = db;

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var cuentas = con.Query<Cuenta>(
            @"SELECT c.id, c.usuario_id AS UsuarioId, c.nombre, c.tipo, c.icono, c.dia_pago AS DiaPago, c.activo,
                     COALESCE((SELECT SUM(CASE
                          WHEN m.tipo='ingreso'      AND m.cuenta_id=c.id         THEN  m.monto
                          WHEN m.tipo='gasto'        AND m.cuenta_id=c.id         THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_id=c.id         THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_destino_id=c.id THEN  m.monto
                          ELSE 0 END)
                        FROM movimientos m
                        WHERE m.usuario_id=@UsuarioId AND (m.cuenta_id=c.id OR m.cuenta_destino_id=c.id)),0) AS Saldo
              FROM cuentas c WHERE c.usuario_id=@UsuarioId
              ORDER BY c.activo DESC, c.tipo, c.nombre",
            new { UsuarioId }).ToList();
        foreach (var c in cuentas.Where(x => x.Tipo == "tarjeta_credito"))
            c.Saldo = -c.Saldo;
        return View(cuentas);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string nombre, string tipo, string? icono, int? diaPago, bool activo = true)
    {
        if (string.IsNullOrWhiteSpace(nombre) || tipo is not ("efectivo" or "debito" or "tarjeta_credito"))
        {
            TempData["Error"] = "Datos incompletos.";
            return RedirectToAction("Index");
        }
        if (tipo != "tarjeta_credito") diaPago = null;
        icono = ValidarIcono(icono, tipo == "tarjeta_credito" ? "bi-credit-card" : tipo == "efectivo" ? "bi-cash-coin" : "bi-bank");

        using var con = _db.Abrir();
        if (id == 0)
        {
            con.Execute(@"INSERT INTO cuentas (usuario_id, nombre, tipo, icono, dia_pago, activo)
                          VALUES (@UsuarioId, @nombre, @tipo, @icono, @diaPago, @activo)",
                new { UsuarioId, nombre = nombre.Trim(), tipo, icono, diaPago, activo });
            TempData["Ok"] = "Cuenta creada.";
        }
        else
        {
            con.Execute(@"UPDATE cuentas SET nombre=@nombre, tipo=@tipo, icono=@icono, dia_pago=@diaPago, activo=@activo
                          WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, nombre = nombre.Trim(), tipo, icono, diaPago, activo });
            TempData["Ok"] = "Cuenta actualizada.";
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        var tieneMovimientos = con.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM movimientos
              WHERE usuario_id=@UsuarioId AND (cuenta_id=@id OR cuenta_destino_id=@id)",
            new { id, UsuarioId }) > 0;

        if (tieneMovimientos)
        {
            // No se borra para no perder historial: se inactiva.
            con.Execute("UPDATE cuentas SET activo=FALSE WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
            TempData["Ok"] = "La cuenta tiene movimientos: se marco como inactiva para conservar el historial.";
        }
        else
        {
            con.Execute("DELETE FROM cuentas WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
            TempData["Ok"] = "Cuenta eliminada.";
        }
        return RedirectToAction("Index");
    }

    private static string ValidarIcono(string? icono, string predeterminado) =>
        !string.IsNullOrWhiteSpace(icono) && icono.StartsWith("bi-", StringComparison.Ordinal) && icono.Length <= 50
            ? icono : predeterminado;
}
