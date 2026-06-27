using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class PersonasController : BaseController
{
    private readonly Db _db;
    public PersonasController(Db db) => _db = db;

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var personas = con.Query<Persona>(
            @"SELECT p.id, p.usuario_id AS UsuarioId, p.nombre, p.telefono, p.email, p.documento, p.notas, p.activo,
                     (SELECT COUNT(*) FROM prestamos pr
                      WHERE pr.persona_id = p.id AND pr.usuario_id = @UsuarioId AND pr.estado = 'activo') AS PrestamosActivos,
                     COALESCE((SELECT SUM(GREATEST(0,pr.capital - COALESCE(
                         (SELECT SUM(pp.monto) FROM prestamo_pagos pp
                          WHERE pp.prestamo_id=pr.id AND pp.usuario_id=@UsuarioId AND pp.tipo='abono_capital'),0)))
                      FROM prestamos pr
                      WHERE pr.persona_id=p.id AND pr.usuario_id=@UsuarioId AND pr.estado='activo'),0) AS DeudaActual
              FROM personas p
              WHERE p.usuario_id = @UsuarioId
              ORDER BY p.activo DESC, p.nombre", new { UsuarioId }).ToList();
        return View(personas);
    }

    public IActionResult PrestamosPersona(int id)
    {
        using var con = _db.Abrir();
        var ids = con.Query<int>(
            @"SELECT id FROM prestamos
              WHERE persona_id=@id AND usuario_id=@UsuarioId AND estado='activo'
              ORDER BY fecha DESC", new { id, UsuarioId }).ToList();
        if (ids.Count == 1) return RedirectToAction("Detalle", "Prestamos", new { id = ids[0] });
        return RedirectToAction("Index", "Prestamos", new { personaId = id, estado = "activo" });
    }

    public IActionResult Resumen(int id)
    {
        using var con = _db.Abrir();
        var persona = con.QueryFirstOrDefault<Persona>(
            @"SELECT id,usuario_id AS UsuarioId,nombre,telefono,email,documento,notas,activo
              FROM personas WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        if (persona == null) return NotFound();
        var prestamos = con.Query<Prestamo>(
            @"SELECT p.id,p.usuario_id AS UsuarioId,p.persona_id AS PersonaId,p.fecha,p.capital,
              p.tasa_mensual AS TasaMensual,p.dia_pago_interes AS DiaPagoInteres,
              p.fecha_pago_capital AS FechaPagoCapital,p.notas,p.estado,pe.nombre AS PersonaNombre
              FROM prestamos p JOIN personas pe ON pe.id=p.persona_id
              WHERE p.persona_id=@id AND p.usuario_id=@UsuarioId ORDER BY p.fecha DESC",
            new { id, UsuarioId }).ToList();
        var pagos = con.Query<PagoPersonaVm>(
            @"SELECT pp.id,pp.usuario_id AS UsuarioId,pp.prestamo_id AS PrestamoId,pp.fecha,pp.tipo,pp.monto,pp.notas,
              CONCAT('Prestamo ',TO_CHAR(p.fecha,'DD/MM/YYYY'),' · ',TO_CHAR(p.capital,'FM999G999G999')) AS PrestamoReferencia
              FROM prestamo_pagos pp JOIN prestamos p ON p.id=pp.prestamo_id
              WHERE p.persona_id=@id AND pp.usuario_id=@UsuarioId ORDER BY pp.fecha DESC,pp.id DESC",
            new { id, UsuarioId }).ToList();
        var porPrestamo = pagos.Cast<PrestamoPago>().ToLookup(x => x.PrestamoId);
        foreach (var prestamo in prestamos) CalculoPrestamos.CompletarCalculos(prestamo, porPrestamo[prestamo.Id].ToList());
        return View(new PersonaResumenVm
        {
            Persona=persona,Prestamos=prestamos,Pagos=pagos,
            ProximaFechaCobro=prestamos.Where(x=>x.Estado=="activo")
                .Select(ProximaFechaCobro).Where(x=>x.HasValue).Min()
        });
    }

    private static DateTime? ProximaFechaCobro(Prestamo prestamo)
    {
        var candidatos = new List<DateTime>();
        if (prestamo.DiaPagoInteres.HasValue)
        {
            var hoy = DateTime.Today;
            var fecha = new DateTime(hoy.Year,hoy.Month,Math.Min(prestamo.DiaPagoInteres.Value,DateTime.DaysInMonth(hoy.Year,hoy.Month)));
            if (fecha < hoy)
            {
                var siguiente = hoy.AddMonths(1);
                fecha = new DateTime(siguiente.Year,siguiente.Month,Math.Min(prestamo.DiaPagoInteres.Value,DateTime.DaysInMonth(siguiente.Year,siguiente.Month)));
            }
            candidatos.Add(fecha);
        }
        if (prestamo.FechaPagoCapital.HasValue && prestamo.FechaPagoCapital.Value.Date >= DateTime.Today)
            candidatos.Add(prestamo.FechaPagoCapital.Value.Date);
        return candidatos.Count > 0 ? candidatos.Min() : null;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string nombre, string? telefono, string? email, string? documento, string? notas, bool activo = true)
    {
        if (string.IsNullOrWhiteSpace(nombre))
        {
            TempData["Error"] = "El nombre es obligatorio.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        if (id == 0)
        {
            con.Execute(
                @"INSERT INTO personas (usuario_id, nombre, telefono, email, documento, notas, activo)
                  VALUES (@UsuarioId, @nombre, @telefono, @email, @documento, @notas, @activo)",
                new { UsuarioId, nombre = nombre.Trim(), telefono, email, documento, notas, activo });
            TempData["Ok"] = "Persona creada.";
        }
        else
        {
            con.Execute(
                @"UPDATE personas SET nombre=@nombre, telefono=@telefono, email=@email,
                         documento=@documento, notas=@notas, activo=@activo
                  WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, nombre = nombre.Trim(), telefono, email, documento, notas, activo });
            TempData["Ok"] = "Persona actualizada.";
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        var tienePrestamos = con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM prestamos WHERE persona_id=@id AND usuario_id=@UsuarioId",
            new { id, UsuarioId }) > 0;

        if (tienePrestamos)
        {
            con.Execute("UPDATE personas SET activo=FALSE WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
            TempData["Ok"] = "La persona tiene prestamos asociados: se marco como inactiva para conservar el historial.";
        }
        else
        {
            con.Execute("DELETE FROM personas WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
            TempData["Ok"] = "Persona eliminada.";
        }
        return RedirectToAction("Index");
    }
}
