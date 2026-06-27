using ClosedXML.Excel;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class MovimientosController : BaseController
{
    private readonly Db _db;
    public MovimientosController(Db db) => _db = db;

    public IActionResult Index(DateTime? desde, DateTime? hasta, string? tipo, int? cuentaId, int? categoriaId, int? anio, int? mes)
    {
        var (d, h) = ResolverRango(desde, hasta, anio, mes);
        var vm = new MovimientosIndexVm
        {
            Desde = d,
            Hasta = h,
            FiltroTipo = tipo,
            FiltroCuentaId = cuentaId,
            FiltroCategoriaId = categoriaId
        };

        using var con = _db.Abrir();
        vm.Movimientos = Consultar(con, UsuarioId, d, h.AddDays(1), tipo, cuentaId, categoriaId);
        vm.Cuentas = CuentasActivas(con);
        vm.Categorias = CategoriasActivas(con);
        vm.TotalIngresos = vm.Movimientos.Where(x => x.Tipo == "ingreso").Sum(x => x.Monto);
        vm.TotalGastos = vm.Movimientos.Where(x => x.Tipo == "gasto").Sum(x => x.Monto);
        vm.TotalPagosTarjeta = vm.Movimientos.Where(x => x.Tipo == "pago_tarjeta").Sum(x => x.Monto);
        vm.TotalTransferencias = vm.Movimientos.Where(x => x.Tipo == "transferencia").Sum(x => x.Monto);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, DateTime fecha, string tipo, int cuentaId,
        int? cuentaDestinoId, int? categoriaId, string? descripcion, decimal monto)
    {
        if (monto <= 0) { TempData["Error"] = "El monto debe ser mayor que cero."; return VolverAlIndice(fecha); }
        if (tipo is not ("ingreso" or "gasto" or "pago_tarjeta" or "transferencia")) return BadRequest();

        using var con = _db.Abrir();

        // Validar que las cuentas y categoria pertenezcan al usuario.
        if (!CuentaEsMia(con, cuentaId)) return Forbid();
        if (tipo is "pago_tarjeta" or "transferencia")
        {
            if (cuentaDestinoId == null || !CuentaEsMia(con, cuentaDestinoId.Value)) return Forbid();
            if (cuentaDestinoId == cuentaId)
            {
                TempData["Error"] = "La cuenta origen y destino no pueden ser la misma.";
                return VolverAlIndice(fecha);
            }
            categoriaId = null;
        }
        else
        {
            cuentaDestinoId = null;
            if (categoriaId == null || !CategoriaCoincideTipo(con, categoriaId.Value, tipo)) return Forbid();
        }

        if (id == 0)
        {
            con.Execute(
                @"INSERT INTO movimientos (usuario_id, fecha, tipo, cuenta_id, cuenta_destino_id, categoria_id, descripcion, monto)
                  VALUES (@UsuarioId, @fecha, @tipo, @cuentaId, @cuentaDestinoId, @categoriaId, @descripcion, @monto)",
                new { UsuarioId, fecha, tipo, cuentaId, cuentaDestinoId, categoriaId, descripcion, monto });
            TempData["Ok"] = "Movimiento registrado.";
        }
        else
        {
            var filas = con.Execute(
                @"UPDATE movimientos SET fecha=@fecha, tipo=@tipo, cuenta_id=@cuentaId,
                         cuenta_destino_id=@cuentaDestinoId, categoria_id=@categoriaId,
                         descripcion=@descripcion, monto=@monto
                  WHERE id=@id AND usuario_id=@UsuarioId",
                new { id, UsuarioId, fecha, tipo, cuentaId, cuentaDestinoId, categoriaId, descripcion, monto });
            TempData[filas > 0 ? "Ok" : "Error"] = filas > 0 ? "Movimiento actualizado." : "No se encontro el movimiento.";
        }
        return VolverAlIndice(fecha);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id, DateTime? desde, DateTime? hasta)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM movimientos WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Movimiento eliminado.";
        return RedirectToAction("Index", new { desde = desde?.ToString("yyyy-MM-dd"), hasta = hasta?.ToString("yyyy-MM-dd") });
    }

    public IActionResult ExportarExcel(DateTime? desde, DateTime? hasta, string? tipo, int? cuentaId, int? categoriaId)
    {
        var (d, h) = ResolverRango(desde, hasta, null, null);

        using var con = _db.Abrir();
        var datos = Consultar(con, UsuarioId, d, h.AddDays(1), tipo, cuentaId, categoriaId);

        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add($"Mov {d:yyyy-MM-dd} a {h:yyyy-MM-dd}");
        string[] encabezados = { "Fecha", "Tipo", "Cuenta", "Cuenta destino", "Categoria", "Descripcion", "Monto" };
        for (var i = 0; i < encabezados.Length; i++)
        {
            hoja.Cell(1, i + 1).Value = encabezados[i];
            hoja.Cell(1, i + 1).Style.Font.Bold = true;
            hoja.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#0d6efd");
            hoja.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }

        var fila = 2;
        foreach (var mv in datos)
        {
            hoja.Cell(fila, 1).Value = mv.Fecha;
            hoja.Cell(fila, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            hoja.Cell(fila, 2).Value = mv.TipoTexto;
            hoja.Cell(fila, 3).Value = mv.CuentaNombre;
            hoja.Cell(fila, 4).Value = mv.CuentaDestinoNombre;
            hoja.Cell(fila, 5).Value = mv.CategoriaNombre;
            hoja.Cell(fila, 6).Value = mv.Descripcion;
            hoja.Cell(fila, 7).Value = mv.Tipo is "ingreso" or "transferencia" ? mv.Monto : -mv.Monto;
            hoja.Cell(fila, 7).Style.NumberFormat.Format = "$ #,##0";
            fila++;
        }
        hoja.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        libro.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"movimientos_{d:yyyyMMdd}_{h:yyyyMMdd}.xlsx");
    }

    // ---------- Auxiliares ----------

    // Resuelve el rango de fechas: rango explicito > mes legado (anio/mes) > mes actual.
    private static (DateTime Desde, DateTime Hasta) ResolverRango(DateTime? desde, DateTime? hasta, int? anio, int? mes)
    {
        var hoy = DateTime.Today;
        if (desde.HasValue || hasta.HasValue)
        {
            var d = (desde ?? new DateTime(hoy.Year, hoy.Month, 1)).Date;
            var h = (hasta ?? hoy).Date;
            return h < d ? (h, d) : (d, h);
        }
        var inicio = (anio.HasValue && mes is >= 1 and <= 12)
            ? new DateTime(anio.Value, mes.Value, 1)
            : new DateTime(hoy.Year, hoy.Month, 1);
        return (inicio, inicio.AddMonths(1).AddDays(-1));
    }

    private IActionResult VolverAlIndice(DateTime fecha) =>
        RedirectToAction("Index", new
        {
            desde = new DateTime(fecha.Year, fecha.Month, 1).ToString("yyyy-MM-dd"),
            hasta = new DateTime(fecha.Year, fecha.Month, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")
        });

    private static List<Movimiento> Consultar(System.Data.IDbConnection con, int usuarioId,
        DateTime desde, DateTime hasta, string? tipo, int? cuentaId, int? categoriaId)
    {
        var sql = @"SELECT m.id, m.usuario_id AS UsuarioId, m.fecha, m.tipo, m.cuenta_id AS CuentaId,
                           m.cuenta_destino_id AS CuentaDestinoId, m.categoria_id AS CategoriaId,
                           m.descripcion, m.monto, m.gasto_periodico_id AS GastoPeriodicoId,
                           c.nombre AS CuentaNombre, c.tipo AS CuentaTipo,
                           cd.nombre AS CuentaDestinoNombre,
                           cat.nombre AS CategoriaNombre, cat.color AS CategoriaColor, cat.icono AS CategoriaIcono
                    FROM movimientos m
                    JOIN cuentas c ON c.id = m.cuenta_id
                    LEFT JOIN cuentas cd ON cd.id = m.cuenta_destino_id
                    LEFT JOIN categorias cat ON cat.id = m.categoria_id
                    WHERE m.usuario_id=@usuarioId AND m.fecha>=@desde AND m.fecha<@hasta";
        if (!string.IsNullOrEmpty(tipo)) sql += " AND m.tipo=@tipo";
        if (cuentaId.HasValue) sql += " AND (m.cuenta_id=@cuentaId OR m.cuenta_destino_id=@cuentaId)";
        if (categoriaId.HasValue) sql += " AND m.categoria_id=@categoriaId";
        sql += " ORDER BY m.fecha DESC, m.id DESC";
        return con.Query<Movimiento>(sql, new { usuarioId, desde, hasta, tipo, cuentaId, categoriaId }).ToList();
    }

    private List<Cuenta> CuentasActivas(System.Data.IDbConnection con) =>
        con.Query<Cuenta>(
            @"SELECT id, usuario_id AS UsuarioId, nombre, tipo, dia_pago AS DiaPago, activo
              FROM cuentas WHERE usuario_id=@UsuarioId AND activo ORDER BY tipo, nombre",
            new { UsuarioId }).ToList();

    private List<Categoria> CategoriasActivas(System.Data.IDbConnection con) =>
        con.Query<Categoria>(
            @"SELECT id, usuario_id AS UsuarioId, nombre, tipo, clase, color, icono, activo
              FROM categorias WHERE usuario_id=@UsuarioId AND activo ORDER BY tipo, nombre",
            new { UsuarioId }).ToList();

    private bool CuentaEsMia(System.Data.IDbConnection con, int id) =>
        con.ExecuteScalar<int>("SELECT COUNT(*) FROM cuentas WHERE id=@id AND usuario_id=@UsuarioId",
            new { id, UsuarioId }) > 0;

    private bool CategoriaCoincideTipo(System.Data.IDbConnection con, int id, string tipo) =>
        con.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM categorias WHERE id=@id AND usuario_id=@UsuarioId AND tipo=@tipo",
            new { id, UsuarioId, tipo }) > 0;
}
