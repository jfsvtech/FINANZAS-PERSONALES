using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

// Importacion masiva de movimientos desde texto plano.
// Formato por linea: Tipo  fecha  descripcion  monto
//   Gasto 2026/05/18 pago a papa restante del techo 1.400.000
//   Ingreso 2026/06/02 Pago casa de willi 924.000
public class ImportarController : BaseController
{
    private readonly Db _db;
    public ImportarController(Db db) => _db = db;

    // Tipo (gasto|ingreso) + fecha aaaa/mm/dd o dd/mm/aaaa + descripcion + monto al final.
    private static readonly Regex PatronLinea = new(
        @"^(?<tipo>gasto|ingreso)\s+(?<fecha>\d{1,4}[/-]\d{1,2}[/-]\d{1,4})\s+(?<descripcion>.+?)\s+(?<monto>\d[\d.,]*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IActionResult Index() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Analizar(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            TempData["Error"] = "Pega al menos una linea de texto.";
            return RedirectToAction("Index");
        }

        var vm = new ImportarRevisarVm();
        using var con = _db.Abrir();
        vm.Cuentas = con.Query<Cuenta>(
            @"SELECT id, nombre, tipo FROM cuentas WHERE usuario_id=@UsuarioId AND activo ORDER BY tipo, nombre",
            new { UsuarioId }).ToList();
        vm.Categorias = con.Query<Categoria>(
            @"SELECT id, nombre, tipo FROM categorias WHERE usuario_id=@UsuarioId AND activo ORDER BY tipo, nombre",
            new { UsuarioId }).ToList();

        if (!vm.Cuentas.Any())
        {
            TempData["Error"] = "Primero crea al menos una cuenta para asignar los movimientos.";
            return RedirectToAction("Index");
        }

        foreach (var lineaCruda in texto.Split('\n'))
        {
            var linea = lineaCruda.Trim();
            if (linea.Length == 0) continue;

            var m = PatronLinea.Match(linea);
            if (!m.Success) { vm.LineasConError.Add(linea); continue; }

            if (!TryParseFecha(m.Groups["fecha"].Value, out var fecha) ||
                !TryParseMonto(m.Groups["monto"].Value, out var monto) || monto <= 0)
            {
                vm.LineasConError.Add(linea);
                continue;
            }

            vm.Filas.Add(new FilaImportar
            {
                Tipo = m.Groups["tipo"].Value.ToLower(),
                Fecha = fecha,
                Descripcion = m.Groups["descripcion"].Value.Trim(),
                Monto = monto
            });
        }

        if (!vm.Filas.Any())
        {
            TempData["Error"] = "No se pudo interpretar ninguna linea. Formato esperado: Gasto 2026/05/18 descripcion 1.400.000";
            return RedirectToAction("Index");
        }
        return View("Revisar", vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Guardar(List<FilaImportar> filas)
    {
        if (filas == null || !filas.Any())
        {
            TempData["Error"] = "No llego ninguna fila para guardar.";
            return RedirectToAction("Index");
        }

        using var con = _db.Abrir();
        var cuentasMias = con.Query<int>("SELECT id FROM cuentas WHERE usuario_id=@UsuarioId", new { UsuarioId }).ToHashSet();
        var categoriasMias = con.Query<(int Id, string Tipo)>(
            "SELECT id, tipo FROM categorias WHERE usuario_id=@UsuarioId", new { UsuarioId })
            .ToDictionary(x => x.Id, x => x.Tipo);

        var guardadas = 0;
        var rechazadas = 0;
        foreach (var f in filas.Where(x => x.Incluir))
        {
            var valida = f.Tipo is "gasto" or "ingreso"
                && f.Monto > 0
                && cuentasMias.Contains(f.CuentaId)
                && categoriasMias.TryGetValue(f.CategoriaId, out var tipoCat)
                && tipoCat == (f.Tipo == "ingreso" ? "ingreso" : "gasto");
            if (!valida) { rechazadas++; continue; }

            con.Execute(
                @"INSERT INTO movimientos (usuario_id, fecha, tipo, cuenta_id, categoria_id, descripcion, monto)
                  VALUES (@UsuarioId, @Fecha, @Tipo, @CuentaId, @CategoriaId, @Descripcion, @Monto)",
                new { UsuarioId, f.Fecha, f.Tipo, f.CuentaId, f.CategoriaId, f.Descripcion, f.Monto });
            guardadas++;
        }

        TempData[guardadas > 0 ? "Ok" : "Error"] =
            $"Importacion terminada: {guardadas} movimiento(s) creado(s)" +
            (rechazadas > 0 ? $", {rechazadas} rechazado(s) por datos invalidos." : ".");

        var primera = filas.FirstOrDefault(x => x.Incluir);
        return RedirectToAction("Index", "Movimientos",
            primera == null ? null : new { anio = primera.Fecha.Year, mes = primera.Fecha.Month });
    }

    // Acepta aaaa/mm/dd, aaaa-mm-dd, dd/mm/aaaa y dd-mm-aaaa.
    private static bool TryParseFecha(string texto, out DateTime fecha)
    {
        var formatos = new[] { "yyyy/M/d", "yyyy-M-d", "d/M/yyyy", "d-M-yyyy" };
        return DateTime.TryParseExact(texto, formatos, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
    }

    // Formato colombiano: puntos como separador de miles, coma como decimal (1.400.000 o 35.000,50).
    private static bool TryParseMonto(string texto, out decimal monto)
    {
        var normalizado = texto.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(normalizado, NumberStyles.Number, CultureInfo.InvariantCulture, out monto);
    }
}
