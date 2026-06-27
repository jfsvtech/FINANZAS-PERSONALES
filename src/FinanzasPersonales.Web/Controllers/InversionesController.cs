using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class InversionesController : BaseController
{
    private readonly Db _db;
    public InversionesController(Db db) => _db = db;

    public IActionResult Index(string? estado, int? tipoId)
    {
        using var con = _db.Abrir();
        var vm = new InversionesIndexVm
        {
            FiltroEstado = estado,
            FiltroTipoId = tipoId,
            Inversiones = CargarInversiones(con, estado: estado, tipoId: tipoId),
            Tipos = CargarTipos(con)
        };
        return View(vm);
    }

    public IActionResult Tablero()
    {
        using var con = _db.Abrir();
        var inversiones = CargarInversiones(con);
        var vm = new InversionesTableroVm { Inversiones = inversiones };
        vm.Distribucion = inversiones.Where(x => x.Estado == "activa" && x.ValorActual > 0)
            .GroupBy(x => x.TipoTexto)
            .Select(g => new GastoCategoriaVm
            {
                Nombre = g.Key,
                Total = g.Sum(x => x.ValorActual),
                Color = g.First().TipoColor ?? g.First().Color,
                Icono = g.First().TipoIcono ?? g.First().Icono
            }).OrderByDescending(x => x.Total).ToList();

        var movimientos = con.Query<InversionMovimiento>(
            @"SELECT id,usuario_id AS UsuarioId,inversion_id AS InversionId,fecha,tipo,monto,notas
              FROM inversion_movimientos WHERE usuario_id=@UsuarioId", new { UsuarioId }).ToList();
        var valoraciones = con.Query<InversionValoracion>(
            @"SELECT id,usuario_id AS UsuarioId,inversion_id AS InversionId,fecha,valor,notas
              FROM inversion_valoraciones WHERE usuario_id=@UsuarioId", new { UsuarioId }).ToList();
        var inicio = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-11);
        for (var i = 0; i < 12; i++)
        {
            var fecha = inicio.AddMonths(i + 1).AddDays(-1);
            vm.Evolucion.Add(new InversionSerieVm
            {
                Fecha = fecha,
                Valor = inversiones.Sum(x => ValorEnFecha(x, movimientos.Where(m => m.InversionId == x.Id), valoraciones.Where(v => v.InversionId == x.Id), fecha))
            });
        }
        vm.ProximosEventos = inversiones.Where(x => x.Estado == "activa")
            .Where(x => (x.FechaRetorno.HasValue && x.FechaRetorno.Value.Date <= DateTime.Today.AddDays(60))
                     || (x.EnPermanencia && x.FechaDisponible.Date <= DateTime.Today.AddDays(60)))
            .OrderBy(x => x.FechaRetorno ?? x.FechaDisponible).ToList();
        return View(vm);
    }

    public IActionResult Detalle(int id)
    {
        using var con = _db.Abrir();
        var inversion = CargarInversiones(con, id: id).FirstOrDefault();
        if (inversion == null) return NotFound();
        var vm = new InversionDetalleVm
        {
            Inversion = inversion,
            Movimientos = con.Query<InversionMovimiento>(
                @"SELECT id,usuario_id AS UsuarioId,inversion_id AS InversionId,fecha,tipo,monto,notas
                  FROM inversion_movimientos WHERE inversion_id=@id AND usuario_id=@UsuarioId
                  ORDER BY fecha DESC,id DESC", new { id, UsuarioId }).ToList(),
            Valoraciones = con.Query<InversionValoracion>(
                @"SELECT id,usuario_id AS UsuarioId,inversion_id AS InversionId,fecha,valor,notas
                  FROM inversion_valoraciones WHERE inversion_id=@id AND usuario_id=@UsuarioId
                  ORDER BY fecha DESC,id DESC", new { id, UsuarioId }).ToList()
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(int id, string nombre, string? entidad, int tipoInversionId, DateTime fechaInicio,
        decimal capitalInicial, decimal tasa, string periodoTasa, string tipoRendimiento,
        DateTime? fechaRetorno, int permanenciaMeses, decimal? penalidadRetiro,
        bool renovacionAutomatica, string? color, string? icono, string? notas)
    {
        if (string.IsNullOrWhiteSpace(nombre) || capitalInicial <= 0 || tasa < 0
            || periodoTasa is not ("mensual" or "anual")
            || tipoRendimiento is not ("fijo" or "variable") || permanenciaMeses is < 0 or > 600
            || penalidadRetiro is < 0 or > 100)
        {
            TempData["Error"] = "Revisa los datos de la inversion.";
            return RedirectToAction("Index");
        }
        if (fechaRetorno.HasValue && fechaRetorno.Value.Date < fechaInicio.Date)
        {
            TempData["Error"] = "La fecha de retorno no puede ser anterior al inicio.";
            return RedirectToAction("Index");
        }
        color = ValidarColor(color);
        icono = ValidarIcono(icono);
        using var con = _db.Abrir();
        var tipoInversion = con.QueryFirstOrDefault<TipoInversion>(
            @"SELECT id,usuario_id AS UsuarioId,nombre,color,icono,activo
              FROM tipos_inversion WHERE id=@tipoInversionId AND usuario_id=@UsuarioId",
            new { tipoInversionId, UsuarioId });
        if (tipoInversion == null || (!tipoInversion.Activo && id == 0))
        {
            TempData["Error"] = "Selecciona un tipo de inversion valido.";
            return RedirectToAction("Index");
        }
        if (string.IsNullOrWhiteSpace(color) || color == "#D4AF37") color = tipoInversion.Color;
        if (string.IsNullOrWhiteSpace(icono) || icono == "bi-graph-up-arrow") icono = tipoInversion.Icono;
        var tipo = tipoInversion.Nombre;
        if (id == 0)
        {
            var nuevoId = con.ExecuteScalar<int>(
                @"INSERT INTO inversiones(usuario_id,nombre,entidad,tipo,tipo_inversion_id,fecha_inicio,capital_inicial,tasa,periodo_tasa,
                  tipo_rendimiento,fecha_retorno,permanencia_meses,penalidad_retiro,renovacion_automatica,color,icono,notas)
                  VALUES(@UsuarioId,@nombre,@entidad,@tipo,@tipoInversionId,@fechaInicio,@capitalInicial,@tasa,@periodoTasa,
                  @tipoRendimiento,@fechaRetorno,@permanenciaMeses,@penalidadRetiro,@renovacionAutomatica,@color,@icono,@notas)
                  RETURNING id",
                new { UsuarioId, nombre = nombre.Trim(), entidad = entidad?.Trim(), tipo, tipoInversionId, fechaInicio, capitalInicial, tasa,
                    periodoTasa, tipoRendimiento, fechaRetorno, permanenciaMeses, penalidadRetiro,
                    renovacionAutomatica, color, icono, notas = notas?.Trim() });
            TempData["Ok"] = "Inversion creada.";
            return RedirectToAction("Detalle", new { id = nuevoId });
        }
        con.Execute(
            @"UPDATE inversiones SET nombre=@nombre,entidad=@entidad,tipo=@tipo,tipo_inversion_id=@tipoInversionId,fecha_inicio=@fechaInicio,
              capital_inicial=@capitalInicial,tasa=@tasa,periodo_tasa=@periodoTasa,tipo_rendimiento=@tipoRendimiento,
              fecha_retorno=@fechaRetorno,permanencia_meses=@permanenciaMeses,penalidad_retiro=@penalidadRetiro,
              renovacion_automatica=@renovacionAutomatica,color=@color,icono=@icono,notas=@notas
              WHERE id=@id AND usuario_id=@UsuarioId",
            new { id, UsuarioId, nombre = nombre.Trim(), entidad = entidad?.Trim(), tipo, tipoInversionId, fechaInicio, capitalInicial, tasa,
                periodoTasa, tipoRendimiento, fechaRetorno, permanenciaMeses, penalidadRetiro,
                renovacionAutomatica, color, icono, notas = notas?.Trim() });
        TempData["Ok"] = "Inversion actualizada.";
        return RedirectToAction("Detalle", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RegistrarMovimiento(int inversionId, DateTime fecha, string tipo, decimal monto, string? notas)
    {
        if (tipo is not ("aporte" or "retiro" or "rendimiento" or "costo") || monto <= 0) return BadRequest();
        using var con = _db.Abrir();
        var inversion = CargarInversiones(con, id: inversionId).FirstOrDefault();
        if (inversion == null) return NotFound();
        if (fecha.Date < inversion.FechaInicio.Date)
        {
            TempData["Error"] = "La fecha no puede ser anterior al inicio de la inversion.";
            return RedirectToAction("Detalle", new { id = inversionId });
        }
        if (fecha.Date > DateTime.Today)
        {
            TempData["Error"] = "No puedes registrar un movimiento futuro.";
            return RedirectToAction("Detalle", new { id = inversionId });
        }
        if (tipo is "retiro" or "costo" && monto > inversion.ValorActual)
        {
            TempData["Error"] = $"El monto supera el valor actual ({inversion.ValorActual:C0}).";
            return RedirectToAction("Detalle", new { id = inversionId });
        }
        con.Execute(
            @"INSERT INTO inversion_movimientos(usuario_id,inversion_id,fecha,tipo,monto,notas)
              VALUES(@UsuarioId,@inversionId,@fecha,@tipo,@monto,@notas)",
            new { UsuarioId, inversionId, fecha, tipo, monto, notas = notas?.Trim() });
        TempData["Ok"] = "Movimiento registrado.";
        return RedirectToAction("Detalle", new { id = inversionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RegistrarValoracion(int inversionId, DateTime fecha, decimal valor, string? notas)
    {
        if (valor < 0) return BadRequest();
        using var con = _db.Abrir();
        var inicio = con.ExecuteScalar<DateTime?>(
            "SELECT fecha_inicio FROM inversiones WHERE id=@inversionId AND usuario_id=@UsuarioId",
            new { inversionId, UsuarioId });
        if (!inicio.HasValue) return NotFound();
        if (fecha.Date < inicio.Value.Date)
        {
            TempData["Error"] = "La valoracion no puede ser anterior al inicio.";
            return RedirectToAction("Detalle", new { id = inversionId });
        }
        if (fecha.Date > DateTime.Today)
        {
            TempData["Error"] = "No puedes registrar una valoracion futura.";
            return RedirectToAction("Detalle", new { id = inversionId });
        }
        con.Execute(
            @"INSERT INTO inversion_valoraciones(usuario_id,inversion_id,fecha,valor,notas)
              VALUES(@UsuarioId,@inversionId,@fecha,@valor,@notas)",
            new { UsuarioId, inversionId, fecha, valor, notas = notas?.Trim() });
        TempData["Ok"] = "Valor actual registrado.";
        return RedirectToAction("Detalle", new { id = inversionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EliminarMovimiento(int id, int inversionId)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM inversion_movimientos WHERE id=@id AND inversion_id=@inversionId AND usuario_id=@UsuarioId",
            new { id, inversionId, UsuarioId });
        TempData["Ok"] = "Movimiento eliminado.";
        return RedirectToAction("Detalle", new { id = inversionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult EliminarValoracion(int id, int inversionId)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM inversion_valoraciones WHERE id=@id AND inversion_id=@inversionId AND usuario_id=@UsuarioId",
            new { id, inversionId, UsuarioId });
        TempData["Ok"] = "Valoracion eliminada.";
        return RedirectToAction("Detalle", new { id = inversionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CambiarEstado(int id, string estado)
    {
        if (estado is not ("activa" or "cerrada")) return BadRequest();
        using var con = _db.Abrir();
        con.Execute("UPDATE inversiones SET estado=@estado WHERE id=@id AND usuario_id=@UsuarioId",
            new { id, UsuarioId, estado });
        TempData["Ok"] = estado == "cerrada" ? "Inversion cerrada." : "Inversion reactivada.";
        return RedirectToAction("Detalle", new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Eliminar(int id)
    {
        using var con = _db.Abrir();
        con.Execute("DELETE FROM inversiones WHERE id=@id AND usuario_id=@UsuarioId", new { id, UsuarioId });
        TempData["Ok"] = "Inversion eliminada con su historial.";
        return RedirectToAction("Index");
    }

    private List<Inversion> CargarInversiones(System.Data.IDbConnection con, int? id = null, string? estado = null, int? tipoId = null)
    {
        var sql = @"SELECT i.id,i.usuario_id AS UsuarioId,i.nombre,i.entidad,i.tipo,
                    i.tipo_inversion_id AS TipoInversionId,COALESCE(t.nombre,i.tipo) AS TipoNombre,
                    t.color AS TipoColor,t.icono AS TipoIcono,i.fecha_inicio AS FechaInicio,
                    i.capital_inicial AS CapitalInicial,i.tasa,i.periodo_tasa AS PeriodoTasa,
                    i.tipo_rendimiento AS TipoRendimiento,i.fecha_retorno AS FechaRetorno,
                    i.permanencia_meses AS PermanenciaMeses,i.penalidad_retiro AS PenalidadRetiro,
                    i.renovacion_automatica AS RenovacionAutomatica,i.moneda,i.color,i.icono,i.notas,i.estado
                    FROM inversiones i LEFT JOIN tipos_inversion t ON t.id=i.tipo_inversion_id
                    WHERE i.usuario_id=@UsuarioId";
        if (id.HasValue) sql += " AND i.id=@id";
        if (estado is "activa" or "cerrada") sql += " AND i.estado=@estado";
        if (tipoId.HasValue) sql += " AND i.tipo_inversion_id=@tipoId";
        sql += " ORDER BY i.estado,i.fecha_inicio DESC,i.nombre";
        var inversiones = con.Query<Inversion>(sql, new { UsuarioId, id, estado, tipoId }).ToList();
        if (inversiones.Count == 0) return inversiones;
        var ids = inversiones.Select(x => x.Id).ToArray();
        var movimientos = con.Query<InversionMovimiento>(
            @"SELECT id,usuario_id AS UsuarioId,inversion_id AS InversionId,fecha,tipo,monto,notas
              FROM inversion_movimientos WHERE usuario_id=@UsuarioId AND inversion_id=ANY(@ids)",
            new { UsuarioId, ids }).ToLookup(x => x.InversionId);
        var valoraciones = con.Query<InversionValoracion>(
            @"SELECT id,usuario_id AS UsuarioId,inversion_id AS InversionId,fecha,valor,notas
              FROM inversion_valoraciones WHERE usuario_id=@UsuarioId AND inversion_id=ANY(@ids)",
            new { UsuarioId, ids }).ToLookup(x => x.InversionId);
        foreach (var inversion in inversiones)
            Completar(inversion, movimientos[inversion.Id], valoraciones[inversion.Id]);
        return inversiones;
    }

    private List<TipoInversion> CargarTipos(System.Data.IDbConnection con) =>
        con.Query<TipoInversion>(
            @"SELECT id,usuario_id AS UsuarioId,nombre,color,icono,activo
              FROM tipos_inversion WHERE usuario_id=@UsuarioId
              ORDER BY activo DESC,nombre", new { UsuarioId }).ToList();

    private static void Completar(Inversion inversion, IEnumerable<InversionMovimiento> movimientos, IEnumerable<InversionValoracion> valoraciones)
    {
        var movs = movimientos.Where(x => x.Fecha.Date <= DateTime.Today)
            .OrderBy(x => x.Fecha).ThenBy(x => x.Id).ToList();
        var vals = valoraciones.Where(x => x.Fecha.Date <= DateTime.Today)
            .OrderBy(x => x.Fecha).ThenBy(x => x.Id).ToList();
        inversion.AportesAdicionales = movs.Where(x => x.Tipo == "aporte").Sum(x => x.Monto);
        inversion.TotalRetiros = movs.Where(x => x.Tipo == "retiro").Sum(x => x.Monto);
        inversion.TotalRendimientos = movs.Where(x => x.Tipo == "rendimiento").Sum(x => x.Monto);
        inversion.TotalCostos = movs.Where(x => x.Tipo == "costo").Sum(x => x.Monto);
        var ultima = vals.LastOrDefault();
        inversion.FechaUltimaValoracion = ultima?.Fecha;
        inversion.ValorActual = ultima?.Valor ?? inversion.CapitalInicial;
        var movimientosAplicables = ultima == null ? movs : movs.Where(x => x.Fecha.Date > ultima.Fecha.Date);
        inversion.ValorActual += movimientosAplicables.Sum(x => x.Tipo switch
        {
            "aporte" or "rendimiento" => x.Monto,
            "retiro" or "costo" => -x.Monto,
            _ => 0
        });
        inversion.ValorActual = Math.Max(0, inversion.ValorActual);
        var fin = inversion.FechaRetorno ?? inversion.FechaDisponible;
        inversion.ValorProyectadoRetorno = CalculoInversiones.Proyectar(
            inversion.CapitalAportado, inversion.Tasa, inversion.PeriodoTasa, inversion.FechaInicio, fin);
    }

    private static decimal ValorEnFecha(Inversion inversion, IEnumerable<InversionMovimiento> movimientos,
        IEnumerable<InversionValoracion> valoraciones, DateTime fecha)
    {
        if (fecha.Date < inversion.FechaInicio.Date) return 0;
        var ultima = valoraciones.Where(x => x.Fecha.Date <= fecha.Date)
            .OrderBy(x => x.Fecha).ThenBy(x => x.Id).LastOrDefault();
        var valor = ultima?.Valor ?? inversion.CapitalInicial;
        var movs = movimientos.Where(x => x.Fecha.Date <= fecha.Date && (ultima == null || x.Fecha.Date > ultima.Fecha.Date));
        valor += movs.Sum(x => x.Tipo switch
        {
            "aporte" or "rendimiento" => x.Monto,
            "retiro" or "costo" => -x.Monto,
            _ => 0
        });
        return Math.Max(0, valor);
    }

    private static string ValidarColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")
            ? color : "#D4AF37";

    private static string ValidarIcono(string? icono) =>
        !string.IsNullOrWhiteSpace(icono) && icono.StartsWith("bi-", StringComparison.Ordinal) && icono.Length <= 50
            ? icono : "bi-graph-up-arrow";
}
