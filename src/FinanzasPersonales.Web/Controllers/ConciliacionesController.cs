using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class ConciliacionesController : BaseController
{
    private readonly Db _db;
    private readonly AuditoriaService _auditoria;

    public ConciliacionesController(Db db, AuditoriaService auditoria)
    {
        _db = db;
        _auditoria = auditoria;
    }

    public IActionResult Index()
    {
        using var con = _db.Abrir();
        var cuentas = con.Query<Cuenta>(
            @"SELECT c.id,c.usuario_id AS UsuarioId,c.nombre,c.tipo,c.icono,c.activo,
                     COALESCE((SELECT SUM(CASE
                          WHEN m.tipo='ingreso' AND m.cuenta_id=c.id THEN m.monto
                          WHEN m.tipo='gasto' AND m.cuenta_id=c.id THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_id=c.id THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_destino_id=c.id THEN m.monto
                          ELSE 0 END)
                        FROM movimientos m
                        WHERE m.usuario_id=@UsuarioId AND (m.cuenta_id=c.id OR m.cuenta_destino_id=c.id)),0) AS Saldo
              FROM cuentas c
              WHERE c.usuario_id=@UsuarioId AND c.activo
              ORDER BY c.tipo,c.nombre", new { UsuarioId }).ToList();
        foreach (var cuenta in cuentas.Where(x => x.Tipo == "tarjeta_credito"))
            cuenta.Saldo = -cuenta.Saldo;

        var vm = new ConciliacionVm
        {
            Cuentas = cuentas,
            Historial = con.Query<ConciliacionCuenta>(
                @"SELECT cc.id,cc.usuario_id AS UsuarioId,cc.cuenta_id AS CuentaId,c.nombre AS CuentaNombre,
                         c.icono AS CuentaIcono,cc.fecha,cc.saldo_sistema AS SaldoSistema,
                         cc.saldo_real AS SaldoReal,cc.diferencia,cc.notas,cc.creado_en AS CreadoEn
                  FROM conciliaciones_cuenta cc
                  JOIN cuentas c ON c.id=cc.cuenta_id
                  WHERE cc.usuario_id=@UsuarioId
                  ORDER BY cc.fecha DESC,cc.id DESC
                  LIMIT 30", new { UsuarioId }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int cuentaId, decimal saldoReal, DateTime? fecha, string? notas)
    {
        using var con = _db.Abrir();
        var cuenta = con.QueryFirstOrDefault<Cuenta>(
            @"SELECT c.id,c.usuario_id AS UsuarioId,c.nombre,c.tipo,c.icono,
                     COALESCE((SELECT SUM(CASE
                          WHEN m.tipo='ingreso' AND m.cuenta_id=c.id THEN m.monto
                          WHEN m.tipo='gasto' AND m.cuenta_id=c.id THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_id=c.id THEN -m.monto
                          WHEN m.tipo IN ('pago_tarjeta','transferencia') AND m.cuenta_destino_id=c.id THEN m.monto
                          ELSE 0 END)
                        FROM movimientos m
                        WHERE m.usuario_id=@UsuarioId AND (m.cuenta_id=c.id OR m.cuenta_destino_id=c.id)),0) AS Saldo
              FROM cuentas c
              WHERE c.usuario_id=@UsuarioId AND c.id=@cuentaId", new { UsuarioId, cuentaId });
        if (cuenta == null) return Forbid();
        if (cuenta.Tipo == "tarjeta_credito") cuenta.Saldo = -cuenta.Saldo;

        var diferencia = saldoReal - cuenta.Saldo;
        var id = con.ExecuteScalar<int>(
            @"INSERT INTO conciliaciones_cuenta(usuario_id,cuenta_id,fecha,saldo_sistema,saldo_real,diferencia,notas)
              VALUES(@UsuarioId,@cuentaId,@fecha,@saldoSistema,@saldoReal,@diferencia,@notas)
              RETURNING id",
            new
            {
                UsuarioId,
                cuentaId,
                fecha = (fecha ?? DateTime.Today).Date,
                saldoSistema = cuenta.Saldo,
                saldoReal,
                diferencia,
                notas = (notas ?? "").Trim()
            });
        _auditoria.Registrar(UsuarioId, UsuarioId, "Finanzas", "Conciliacion", "conciliaciones_cuenta", id, $"Conciliada {cuenta.Nombre}. Diferencia {diferencia:N0}.");
        TempData[Math.Abs(diferencia) <= 1 ? "Ok" : "Error"] = Math.Abs(diferencia) <= 1
            ? "Cuenta conciliada sin diferencias importantes."
            : $"Conciliacion guardada con diferencia de {diferencia:C0}. Revisa movimientos faltantes o duplicados.";
        return RedirectToAction("Index");
    }
}
