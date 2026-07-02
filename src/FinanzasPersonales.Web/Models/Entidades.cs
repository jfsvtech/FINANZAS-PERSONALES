namespace FinanzasPersonales.Web.Models;

public class Usuario
{
    public int Id { get; set; }
    public string NombreUsuario { get; set; } = "";
    public string Email { get; set; } = "";
    public string NombreCompleto { get; set; } = "";
    public string ClaveHash { get; set; } = "";
    public bool EsAdmin { get; set; }
    public bool Activo { get; set; }
    public bool EmailConfirmado { get; set; }
    public int IntentosFallidos { get; set; }
    public DateTime? BloqueadoHasta { get; set; }
    public DateTime? UltimoAcceso { get; set; }
    public DateTime CreadoEn { get; set; }
    public decimal ValorSuscripcion { get; set; }
    public string CicloSuscripcion { get; set; } = "mensual";
    public DateTime? FechaInicioSuscripcion { get; set; }
    public DateTime? ProximoPago { get; set; }
    public int DiasGracia { get; set; } = 3;
    public string EstadoSuscripcion { get; set; } = "activa";
    public string? NotasSuscripcion { get; set; }
    public bool SuspendidoPorMora { get; set; }
    public DateTime? SuspendidoEn { get; set; }
    public decimal TotalPagadoSuscripcion { get; set; }
    public DateTime? UltimoPagoSuscripcion { get; set; }
    public bool PermisoGastos { get; set; } = true;
    public bool PermisoPrestamos { get; set; } = true;
    public bool PermisoInversiones { get; set; } = true;
    public bool PermisoDirectivo { get; set; }
    public bool PermisoAsistente { get; set; }
    public bool PermisoCalendario { get; set; }
    public string Idioma { get; set; } = "es";
    public string MonedaCodigo { get; set; } = "COP";
    public string ZonaHoraria { get; set; } = "America/Bogota";
    public int DiasMora => ProximoPago.HasValue && DateTime.Today > ProximoPago.Value.Date.AddDays(DiasGracia)
        ? (DateTime.Today - ProximoPago.Value.Date.AddDays(DiasGracia)).Days
        : 0;
}

public class Moneda
{
    public string Codigo { get; set; } = "COP";
    public string Nombre { get; set; } = "";
    public string Simbolo { get; set; } = "$";
    public int Decimales { get; set; }
    public string Cultura { get; set; } = "es-CO";
    public bool Activa { get; set; } = true;
}

public class IdiomaDisponible
{
    public string Codigo { get; set; } = "es";
    public string Nombre { get; set; } = "Español";
    public string Cultura { get; set; } = "es-CO";
}

public class TasaCambio
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public string MonedaOrigen { get; set; } = "USD";
    public string MonedaDestino { get; set; } = "COP";
    public decimal Tasa { get; set; }
    public string Fuente { get; set; } = "";
    public DateTime CreadoEn { get; set; }
}

public class PagoSuscripcion
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public DateTime FechaPago { get; set; }
    public decimal Monto { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public string PeriodoCubierto { get; set; } = "";
    public string Metodo { get; set; } = "";
    public string? Referencia { get; set; }
    public string? Notas { get; set; }
    public DateTime CreadoEn { get; set; }
}

public class UsuariosAdminVm
{
    public List<Usuario> Usuarios { get; set; } = new();
    public List<PagoSuscripcion> Pagos { get; set; } = new();
    public List<Moneda> Monedas { get; set; } = new();
    public List<IdiomaDisponible> Idiomas { get; set; } = new();
    public bool IncluirInactivos { get; set; }
    public int InactivosOcultos { get; set; }
    public int TotalClientes { get; set; }
    public decimal IngresoMensualEsperado => Usuarios.Where(x => x.Activo && x.EstadoSuscripcion == "activa").Sum(x => x.ValorSuscripcion);
    public decimal PagadoEsteMes => Pagos.Where(x => x.FechaPago.Year == DateTime.Today.Year && x.FechaPago.Month == DateTime.Today.Month).Sum(x => x.Monto);
    public int ClientesActivos => Usuarios.Count(x => x.Activo && x.EstadoSuscripcion == "activa");
    public int ClientesEnMora => Usuarios.Count(x => x.DiasMora > 0 || x.EstadoSuscripcion == "moroso");
}

public class Cuenta
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "debito"; // efectivo | debito | tarjeta_credito
    public int? DiaPago { get; set; }
    public string Icono { get; set; } = "bi-bank";
    public bool Activo { get; set; } = true;
    public decimal Saldo { get; set; } // calculado: saldo disponible o deuda segun tipo

    public string TipoTexto => Tipo switch
    {
        "efectivo" => "Efectivo",
        "debito" => "Cuenta debito",
        "tarjeta_credito" => "Tarjeta de credito",
        _ => Tipo
    };
}

public class Categoria
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "gasto"; // ingreso | gasto
    public string Clase { get; set; } = "variable"; // fijo | variable | periodico
    public string Color { get; set; } = "#6c757d";
    public string Icono { get; set; } = "bi-tag";
    public bool Activo { get; set; } = true;
}

public class Movimiento
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "gasto"; // ingreso | gasto | pago_tarjeta
    public int CuentaId { get; set; }
    public int? CuentaDestinoId { get; set; }
    public int? CategoriaId { get; set; }
    public string? Descripcion { get; set; }
    public decimal Monto { get; set; }
    public decimal MontoOriginal { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public decimal TasaConversion { get; set; } = 1;
    public string MonedaBaseCodigo { get; set; } = "COP";
    public int? GastoPeriodicoId { get; set; }

    // Campos de presentacion (join)
    public string? CuentaNombre { get; set; }
    public string? CuentaTipo { get; set; }
    public string? CuentaDestinoNombre { get; set; }
    public string? CategoriaNombre { get; set; }
    public string? CategoriaColor { get; set; }
    public string CategoriaIcono { get; set; } = "bi-tag";

    public string TipoTexto => Tipo switch
    {
        "ingreso" => "Ingreso",
        "gasto" => "Gasto",
        "pago_tarjeta" => "Pago tarjeta",
        "transferencia" => "Transferencia",
        _ => Tipo
    };
}

public class Presupuesto
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public int CategoriaId { get; set; }
    public decimal MontoMensual { get; set; }
    public string? CategoriaNombre { get; set; }
    public string? CategoriaColor { get; set; }
    public string CategoriaIcono { get; set; } = "bi-tag";
    public decimal Gastado { get; set; } // calculado para el mes consultado
}

public class GastoPeriodico
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Tipo { get; set; } = "gasto";
    public string Nombre { get; set; } = "";
    public int CategoriaId { get; set; }
    public int? CuentaId { get; set; }
    public decimal MontoEstimado { get; set; }
    public int FrecuenciaMeses { get; set; } = 1;
    public DateTime ProximaFecha { get; set; }
    public bool Activo { get; set; } = true;
    public string? CategoriaNombre { get; set; }
    public string CategoriaIcono { get; set; } = "bi-tag";
    public string? CuentaNombre { get; set; }
}

public class MetaAhorro
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = "";
    public decimal MontoObjetivo { get; set; }
    public DateTime? FechaObjetivo { get; set; }
    public string Color { get; set; } = "#6f42c1";
    public bool Activo { get; set; } = true;
    public decimal Ahorrado { get; set; }
    public decimal Restante => Math.Max(0, MontoObjetivo - Ahorrado);
    public int Porcentaje => MontoObjetivo > 0 ? Math.Min(100, (int)(Ahorrado * 100 / MontoObjetivo)) : 0;
    public decimal AporteMensualSugerido
    {
        get
        {
            if (!FechaObjetivo.HasValue || Restante <= 0) return 0;
            var hoy = DateTime.Today;
            var meses = Math.Max(1, ((FechaObjetivo.Value.Year - hoy.Year) * 12) + FechaObjetivo.Value.Month - hoy.Month);
            return Math.Ceiling(Restante / meses);
        }
    }
}

public class AporteMeta
{
    public int Id { get; set; }
    public int MetaAhorroId { get; set; }
    public DateTime Fecha { get; set; }
    public decimal Monto { get; set; }
    public string? Notas { get; set; }
}

public class AlertaFinancieraVm
{
    public string Tipo { get; set; } = "info";
    public string Icono { get; set; } = "bi-info-circle";
    public string Titulo { get; set; } = "";
    public string Detalle { get; set; } = "";
    public string Controller { get; set; } = "Inicio";
    public string Action { get; set; } = "Index";
}

public class ComparacionCategoriaVm
{
    public string Nombre { get; set; } = "";
    public string Color { get; set; } = "#6c757d";
    public string Icono { get; set; } = "bi-tag";
    public decimal Actual { get; set; }
    public decimal Anterior { get; set; }
    public decimal Diferencia => Actual - Anterior;
    public decimal Porcentaje => Anterior > 0 ? Math.Round(Diferencia * 100 / Anterior, 0) : Actual > 0 ? 100 : 0;
}

public class FlujoDiaVm
{
    public int Dia { get; set; }
    public decimal Balance { get; set; }
}

// ---------- ViewModels ----------

public class DashboardVm
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal IngresosMes { get; set; }
    public decimal GastosMes { get; set; }
    public decimal GastosTarjetaMes { get; set; }
    public decimal PagosTarjetaMes { get; set; }
    public decimal SalidasCajaMes { get; set; }
    public decimal Balance => IngresosMes - SalidasCajaMes;
    public decimal SaldoAnterior { get; set; }
    public bool IncluirSaldoAnterior { get; set; }
    public decimal DisponibleMes => Balance + (IncluirSaldoAnterior ? SaldoAnterior : 0);
    public decimal DeudaTarjetas { get; set; }
    public decimal IngresosMesAnterior { get; set; }
    public decimal GastosMesAnterior { get; set; }
    public decimal TasaAhorro => IngresosMes > 0 ? Math.Round(Balance * 100 / IngresosMes, 1) : 0;
    public decimal GastosFijos { get; set; }
    public decimal GastosVariables { get; set; }
    public List<Cuenta> Cuentas { get; set; } = new();
    public List<GastoCategoriaVm> GastosPorCategoria { get; set; } = new();
    public List<SerieMesVm> SerieMensual { get; set; } = new();
    public List<Presupuesto> Presupuestos { get; set; } = new();
    public List<GastoPeriodico> PeriodicosPendientes { get; set; } = new();
    public List<ComparacionCategoriaVm> ComparacionCategorias { get; set; } = new();
    public List<FlujoDiaVm> FlujoDiario { get; set; } = new();
    public List<AlertaFinancieraVm> Alertas { get; set; } = new();
}

public class GastoCategoriaVm
{
    public string Nombre { get; set; } = "";
    public string Color { get; set; } = "#6c757d";
    public string Icono { get; set; } = "bi-tag";
    public decimal Total { get; set; }
}

public class SerieMesVm
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal Ingresos { get; set; }
    public decimal Gastos { get; set; }
    public string Etiqueta => new DateTime(Anio, Mes, 1).ToString("MMM yyyy");
}

public class MovimientosIndexVm
{
    public List<Movimiento> Movimientos { get; set; } = new();
    public List<Cuenta> Cuentas { get; set; } = new();
    public List<Categoria> Categorias { get; set; } = new();
    public List<Moneda> Monedas { get; set; } = new();
    public string MonedaBase { get; set; } = "COP";
    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }
    public string? FiltroTipo { get; set; }
    public int? FiltroCuentaId { get; set; }
    public int? FiltroCategoriaId { get; set; }
    public bool FiltroFlujoCaja { get; set; }
    public string? FiltroCuentaTipo { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalGastos { get; set; }
    public decimal TotalPagosTarjeta { get; set; }
    public decimal TotalTransferencias { get; set; }
}

// ---------- Importacion masiva desde texto ----------

public class FilaImportar
{
    public bool Incluir { get; set; } = true;
    public string Tipo { get; set; } = "gasto"; // gasto | ingreso
    public DateTime Fecha { get; set; }
    public string Descripcion { get; set; } = "";
    public decimal Monto { get; set; }
    public int CuentaId { get; set; }
    public int CategoriaId { get; set; }
}

public class ImportarRevisarVm
{
    public List<FilaImportar> Filas { get; set; } = new();
    public List<string> LineasConError { get; set; } = new();
    public List<Cuenta> Cuentas { get; set; } = new();
    public List<Categoria> Categorias { get; set; } = new();
}

// ---------- Modulo de prestamos ----------

public class Persona
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Documento { get; set; }
    public string? Notas { get; set; }
    public bool Activo { get; set; } = true;
    public int PrestamosActivos { get; set; } // calculado
    public decimal DeudaActual { get; set; } // capital pendiente de prestamos activos
}

public class Prestamo
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public int PersonaId { get; set; }
    public DateTime Fecha { get; set; }
    public decimal Capital { get; set; }
    public decimal? CapitalOriginal { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public decimal TasaConversion { get; set; } = 1;
    public decimal TasaMensual { get; set; } // % de interes mensual
    public int? DiaPagoInteres { get; set; }
    public DateTime? FechaPagoCapital { get; set; }
    public string? Notas { get; set; }
    public string Estado { get; set; } = "activo"; // activo | pagado

    // Presentacion (join)
    public string? PersonaNombre { get; set; }

    // Calculados sobre el historial de pagos
    public decimal AbonadoCapital { get; set; }
    public decimal SaldoCapital { get; set; }
    public decimal InteresDevengado { get; set; }
    public decimal InteresPagado { get; set; }
    public decimal InteresPendiente => Math.Max(0, InteresDevengado - InteresPagado);
    public decimal InteresMensualActual => Math.Round(SaldoCapital * TasaMensual / 100m, 0);
}

public class PrestamoPago
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public int PrestamoId { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "pago_interes"; // abono_capital | pago_interes
    public decimal Monto { get; set; }
    public decimal MontoOriginal { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public decimal TasaConversion { get; set; } = 1;
    public string MonedaBaseCodigo { get; set; } = "COP";
    public string? Notas { get; set; }
    public string TipoTexto => Tipo == "abono_capital" ? "Abono a capital" : "Pago de interes";
}

public class PrestamosIndexVm
{
    public List<Prestamo> Prestamos { get; set; } = new();
    public List<Persona> Personas { get; set; } = new();
    public List<Moneda> Monedas { get; set; } = new();
    public string MonedaBase { get; set; } = "COP";
    public int? FiltroPersonaId { get; set; }
    public string? FiltroEstado { get; set; }
    public DateTime? FiltroDesde { get; set; }
    public DateTime? FiltroHasta { get; set; }
    public decimal TotalPrestado => Prestamos.Where(p => p.Estado == "activo").Sum(p => p.Capital);
    public decimal TotalSaldoCapital => Prestamos.Sum(p => p.SaldoCapital);
    public decimal TotalInteresPendiente => Prestamos.Sum(p => p.InteresPendiente);
}

public class PrestamoDetalleVm
{
    public Prestamo Prestamo { get; set; } = new();
    public List<PrestamoPago> Pagos { get; set; } = new();
    public List<Moneda> Monedas { get; set; } = new();
    public string MonedaBase { get; set; } = "COP";
}

public class PagoPersonaVm : PrestamoPago
{
    public string PrestamoReferencia { get; set; } = "";
}

public class PersonaResumenVm
{
    public Persona Persona { get; set; } = new();
    public List<Prestamo> Prestamos { get; set; } = new();
    public List<PagoPersonaVm> Pagos { get; set; } = new();
    public decimal CapitalPendiente => Prestamos.Where(x => x.Estado == "activo").Sum(x => x.SaldoCapital);
    public decimal InteresPendiente => Prestamos.Where(x => x.Estado == "activo").Sum(x => x.InteresPendiente);
    public decimal TotalPagado => Pagos.Sum(x => x.Monto);
    public decimal CapitalPagado => Pagos.Where(x => x.Tipo == "abono_capital").Sum(x => x.Monto);
    public decimal InteresPagado => Pagos.Where(x => x.Tipo == "pago_interes").Sum(x => x.Monto);
    public DateTime? ProximaFechaCobro { get; set; }
}

public class EventoFinancieroVm
{
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "info";
    public string Icono { get; set; } = "bi-calendar-event";
    public string Titulo { get; set; } = "";
    public string Detalle { get; set; } = "";
    public decimal? Monto { get; set; }
    public string Controller { get; set; } = "Inicio";
    public string Action { get; set; } = "Index";
    public int? RouteId { get; set; }
    public string AccionTexto { get; set; } = "Ver";
    public string Color { get; set; } = "#7C3AED";
    public string Url { get; set; } = "";
    public bool Vencido => Fecha.Date < DateTime.Today;
}

public class CalendarioFinancieroVm
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public List<EventoFinancieroVm> EventosMes { get; set; } = new();
    public List<EventoFinancieroVm> ProximosEventos { get; set; } = new();
    public DateTime PrimerDia => new(Anio, Mes, 1);
    public int DiasMes => DateTime.DaysInMonth(Anio, Mes);
    public string Periodo => PrimerDia.ToString("MMMM yyyy");
}

public class InicioVm
{
    public decimal IngresosMes { get; set; }
    public decimal GastosMes { get; set; }
    public decimal GastosTarjetaMes { get; set; }
    public decimal PagosTarjetaMes { get; set; }
    public decimal SalidasCajaMes { get; set; }
    public decimal BalanceMes => IngresosMes - SalidasCajaMes;
    public decimal SaldoAnterior { get; set; }
    public bool IncluirSaldoAnterior { get; set; }
    public decimal DisponibleMes => BalanceMes + (IncluirSaldoAnterior ? SaldoAnterior : 0);
    public decimal DeudaTarjetas { get; set; }
    public int PeriodicosPendientes { get; set; }

    public int PrestamosActivos { get; set; }
    public decimal SaldoPorCobrar { get; set; }
    public decimal InteresPendiente { get; set; }
    public decimal InteresMensualEsperado { get; set; }
    public int PrestamosAtrasados { get; set; }
    public int CuentasActivas { get; set; }
    public int CategoriasActivas { get; set; }
    public int MetasActivas { get; set; }
    public int AlertasPendientes { get; set; }
    public int InversionesActivas { get; set; }
    public decimal CapitalInversiones { get; set; }
    public decimal ValorInversiones { get; set; }
    public decimal GananciaInversiones { get; set; }
    public int RetornosProximos { get; set; }
}

public class MetasIndexVm
{
    public List<MetaAhorro> Metas { get; set; } = new();
    public decimal TotalObjetivo => Metas.Where(x => x.Activo).Sum(x => x.MontoObjetivo);
    public decimal TotalAhorrado => Metas.Where(x => x.Activo).Sum(x => x.Ahorrado);
}

public class DirectivoVm
{
    public decimal Liquidez { get; set; }
    public decimal DeudaTarjetas { get; set; }
    public decimal SaldoPrestamos { get; set; }
    public decimal InteresPendiente { get; set; }
    public decimal MetasAhorradas { get; set; }
    public decimal ValorInversiones { get; set; }
    public decimal IngresosMes { get; set; }
    public decimal GastosMes { get; set; }
    public decimal PatrimonioNeto => Liquidez + SaldoPrestamos + MetasAhorradas + ValorInversiones - DeudaTarjetas;
    public decimal ResultadoMes => IngresosMes - GastosMes;
    public List<SerieMesVm> SerieMensual { get; set; } = new();
    public List<GastoCategoriaVm> Composicion { get; set; } = new();
}

public class CobroMesVm
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal Intereses { get; set; }
    public decimal Capital { get; set; }
    public string Etiqueta => new DateTime(Anio, Mes, 1).ToString("MMM yyyy");
}

public class PrestamosTableroVm
{
    public List<Prestamo> Prestamos { get; set; } = new();
    public List<CobroMesVm> CobrosPorMes { get; set; } = new();
    public List<GastoCategoriaVm> SaldoPorPersona { get; set; } = new();
    public List<Persona> Personas { get; set; } = new();
    public int? FiltroPersonaId { get; set; }
    public DateTime? FiltroDesde { get; set; }
    public DateTime? FiltroHasta { get; set; }
    public decimal InteresCobradoPeriodo => CobrosPorMes.Sum(x => x.Intereses);
    public decimal CapitalRecuperadoPeriodo => CobrosPorMes.Sum(x => x.Capital);

    public List<Prestamo> Activos => Prestamos.Where(p => p.Estado == "activo").ToList();
    public int CantidadActivos => Activos.Count;
    public int CantidadPagados => Prestamos.Count(p => p.Estado == "pagado");
    public decimal TotalPrestadoActivo => Activos.Sum(p => p.Capital);
    public decimal TotalSaldoCapital => Activos.Sum(p => p.SaldoCapital);
    public decimal TotalInteresPendiente => Activos.Sum(p => p.InteresPendiente);
    public decimal TotalInteresCobrado => Prestamos.Sum(p => p.InteresPagado);
    public decimal InteresMensualEsperado => Activos.Sum(p => p.InteresMensualActual);
}

public class RecomendacionFinancieraVm
{
    public string Tipo { get; set; } = "info";
    public string Icono { get; set; } = "bi-lightbulb";
    public string Titulo { get; set; } = "";
    public string Detalle { get; set; } = "";
    public string Accion { get; set; } = "";
    public string Controller { get; set; } = "Dashboard";
    public string Action { get; set; } = "Index";
}

public class InformeMensualVm
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal Ingresos { get; set; }
    public decimal Gastos { get; set; }
    public decimal IngresosAnterior { get; set; }
    public decimal GastosAnterior { get; set; }
    public decimal Balance => Ingresos - Gastos;
    public decimal TasaAhorro => Ingresos > 0 ? Math.Round(Balance * 100 / Ingresos, 1) : 0;
    public List<GastoCategoriaVm> Categorias { get; set; } = new();
    public List<RecomendacionFinancieraVm> Recomendaciones { get; set; } = new();
    public string Periodo => new DateTime(Anio, Mes, 1).ToString("MMMM yyyy");
}

public class RegistroNaturalVm
{
    public string Texto { get; set; } = "";
    public bool Interpretado { get; set; }
    public string Tipo { get; set; } = "gasto";
    public DateTime Fecha { get; set; } = DateTime.Today;
    public decimal Monto { get; set; }
    public string Descripcion { get; set; } = "";
    public int? CuentaId { get; set; }
    public int? CategoriaId { get; set; }
    public List<Cuenta> Cuentas { get; set; } = new();
    public List<Categoria> Categorias { get; set; } = new();
}

public class RecordatorioVm
{
    public int Id { get; set; }
    public string Tipo { get; set; } = "periodico";
    public string Icono { get; set; } = "bi-bell";
    public string Titulo { get; set; } = "";
    public string Detalle { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string Mensaje { get; set; } = "";
    public string MensajeAdmin { get; set; } = "";
    public string WhatsAppUrl => string.IsNullOrWhiteSpace(Telefono) ? "" : $"https://wa.me/{new string(Telefono.Where(char.IsDigit).ToArray())}?text={Uri.EscapeDataString(Mensaje)}";
    public string EmailUrl => string.IsNullOrWhiteSpace(Email) ? "" : $"mailto:{Email}?subject={Uri.EscapeDataString(Titulo)}&body={Uri.EscapeDataString(Mensaje)}";
}

public class AsistenteIndexVm
{
    public List<RecomendacionFinancieraVm> Recomendaciones { get; set; } = new();
    public List<RecordatorioVm> Recordatorios { get; set; } = new();
}

public class ConfiguracionIntegracionesVm
{
    public string EmailProvider { get; set; } = "smtp";
    public bool GmailApiConfigurado { get; set; }
    public string GmailApiFromEmail { get; set; } = "";
    public string GmailApiFromName { get; set; } = "Finanzas Personales";
    public bool EmailApiConfigurado { get; set; }
    public string EmailApiProvider { get; set; } = "resend";
    public string EmailApiFromEmail { get; set; } = "";
    public string EmailApiFromName { get; set; } = "Finanzas Personales";
    public bool EmailApiKeyGuardada { get; set; }
    public bool SmtpConfigurado { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpFrom { get; set; } = "";
    public bool SmtpEnableSsl { get; set; } = true;
    public bool SmtpPasswordGuardada { get; set; }
    public bool SmtpPasswordCifradaEnBaseDatos { get; set; }
    public bool SmtpPasswordDescifrable { get; set; } = true;
    public string SmtpDiagnostico { get; set; } = "";
    public bool WhatsAppConfigurado { get; set; }
    public string WhatsAppPhoneNumberId { get; set; } = "";
    public string WhatsAppGraphApiVersion { get; set; } = "v23.0";
    public string WhatsAppAdminPhone { get; set; } = "";
    public string WhatsAppTemplateName { get; set; } = "";
    public string WhatsAppTemplateLanguage { get; set; } = "es";
    public bool WhatsAppTokenGuardado { get; set; }
    public bool RecordatoriosEmailActivos { get; set; } = true;
    public int RecordatoriosEmailDiasAntes { get; set; } = 3;
}

public class SmtpDiagnostico
{
    public bool TieneConfiguracionBaseDatos { get; set; }
    public bool PasswordCifradaEnBaseDatos { get; set; }
    public bool PasswordDescifrable { get; set; } = true;
    public string Mensaje { get; set; } = "";
}

public class DocumentacionIndexVm
{
    public List<DocumentoAyudaVm> Documentos { get; set; } = new();
    public List<DocumentoSeccionVm> SeccionesFuncionales { get; set; } = new();
    public bool EsAdmin { get; set; }
}

public class DocumentoDetalleVm
{
    public string Codigo { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Html { get; set; } = "";
    public bool PuedeDescargarPdf { get; set; } = true;
}

public class DocumentoAyudaVm
{
    public string Codigo { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Icono { get; set; } = "bi-file-earmark-pdf";
    public string Nivel { get; set; } = "";
}

public class DocumentoSeccionVm
{
    public string Titulo { get; set; } = "";
    public string Descripcion { get; set; } = "";
    public string Icono { get; set; } = "bi-book";
    public List<string> Puntos { get; set; } = new();
}

public class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool EnableSsl { get; set; } = true;
    public bool Configurado => !string.IsNullOrWhiteSpace(Host) &&
                               !string.IsNullOrWhiteSpace(User) &&
                               !string.IsNullOrWhiteSpace(Password);
}

public class EmailApiSettings
{
    public string Provider { get; set; } = "resend";
    public string ApiKey { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "Finanzas Personales";
    public bool Configurado => Provider.Equals("resend", StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(ApiKey) &&
                               !string.IsNullOrWhiteSpace(FromEmail);
}

public class GmailApiSettings
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "Finanzas Personales";
    public bool Configurado => !string.IsNullOrWhiteSpace(ClientId) &&
                               !string.IsNullOrWhiteSpace(ClientSecret) &&
                               !string.IsNullOrWhiteSpace(RefreshToken) &&
                               !string.IsNullOrWhiteSpace(FromEmail);
}

public class WhatsAppSettings
{
    public string PhoneNumberId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string GraphApiVersion { get; set; } = "v23.0";
    public string AdminPhone { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string TemplateLanguage { get; set; } = "es";
    public bool Configurado => !string.IsNullOrWhiteSpace(PhoneNumberId) &&
                               !string.IsNullOrWhiteSpace(AccessToken);
    public bool PlantillaConfigurada => !string.IsNullOrWhiteSpace(TemplateName);
}

public class WhatsAppSendResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? ProviderResponse { get; set; }
}

// ---------- Modulo de inversiones ----------

public class Inversion
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Entidad { get; set; }
    public string Tipo { get; set; } = "otro";
    public int? TipoInversionId { get; set; }
    public string? TipoNombre { get; set; }
    public string? TipoColor { get; set; }
    public string? TipoIcono { get; set; }
    public DateTime FechaInicio { get; set; }
    public decimal CapitalInicial { get; set; }
    public decimal CapitalOriginal { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public decimal TasaConversion { get; set; } = 1;
    public string MonedaBaseCodigo { get; set; } = "COP";
    public decimal Tasa { get; set; }
    public string PeriodoTasa { get; set; } = "anual";
    public string TipoRendimiento { get; set; } = "fijo";
    public DateTime? FechaRetorno { get; set; }
    public int PermanenciaMeses { get; set; }
    public decimal? PenalidadRetiro { get; set; }
    public bool RenovacionAutomatica { get; set; }
    public string Moneda { get; set; } = "COP";
    public string Color { get; set; } = "#D4AF37";
    public string Icono { get; set; } = "bi-graph-up-arrow";
    public string? Notas { get; set; }
    public string Estado { get; set; } = "activa";

    public decimal AportesAdicionales { get; set; }
    public decimal TotalRetiros { get; set; }
    public decimal TotalRendimientos { get; set; }
    public decimal TotalCostos { get; set; }
    public decimal ValorActual { get; set; }
    public DateTime? FechaUltimaValoracion { get; set; }
    public decimal ValorProyectadoRetorno { get; set; }

    public decimal CapitalAportado => CapitalInicial + AportesAdicionales;
    public decimal GananciaReal => ValorActual + TotalRetiros - CapitalAportado;
    public decimal RentabilidadReal => CapitalAportado > 0 ? Math.Round(GananciaReal * 100 / CapitalAportado, 2) : 0;
    public decimal TasaMensualEquivalente => CalculoInversiones.TasaMensual(Tasa, PeriodoTasa);
    public decimal TasaAnualEquivalente => CalculoInversiones.TasaAnual(Tasa, PeriodoTasa);
    public decimal GananciaMensualEsperada => Math.Round(ValorActual * TasaMensualEquivalente / 100m, 0);
    public DateTime FechaDisponible => FechaInicio.AddMonths(PermanenciaMeses);
    public bool EnPermanencia => Estado == "activa" && DateTime.Today < FechaDisponible.Date;
    public string TipoTexto => !string.IsNullOrWhiteSpace(TipoNombre) ? TipoNombre : Tipo switch
    {
        "cdt" => "CDT / deposito a termino",
        "fondo" => "Fondo de inversion",
        "acciones" => "Acciones",
        "etf" => "ETF",
        "cripto" => "Criptoactivo",
        "negocio" => "Participacion en negocio",
        "inmueble" => "Inmueble",
        _ => Tipo
    };
}

public class TipoInversion
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public string Nombre { get; set; } = "";
    public string Color { get; set; } = "#D4AF37";
    public string Icono { get; set; } = "bi-graph-up-arrow";
    public bool Activo { get; set; } = true;
    public int CantidadInversiones { get; set; }
}

public class InversionMovimiento
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public int InversionId { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "aporte";
    public decimal Monto { get; set; }
    public decimal MontoOriginal { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public decimal TasaConversion { get; set; } = 1;
    public string MonedaBaseCodigo { get; set; } = "COP";
    public string? Notas { get; set; }
    public string TipoTexto => Tipo switch
    {
        "aporte" => "Aporte adicional",
        "retiro" => "Retiro",
        "rendimiento" => "Rendimiento",
        "costo" => "Costo / impuesto",
        _ => Tipo
    };
}

public class InversionValoracion
{
    public int Id { get; set; }
    public int UsuarioId { get; set; }
    public int InversionId { get; set; }
    public DateTime Fecha { get; set; }
    public decimal Valor { get; set; }
    public decimal ValorOriginal { get; set; }
    public string MonedaCodigo { get; set; } = "COP";
    public decimal TasaConversion { get; set; } = 1;
    public string MonedaBaseCodigo { get; set; } = "COP";
    public string? Notas { get; set; }
}

public class InversionesIndexVm
{
    public List<Inversion> Inversiones { get; set; } = new();
    public List<TipoInversion> Tipos { get; set; } = new();
    public List<Moneda> Monedas { get; set; } = new();
    public string MonedaBase { get; set; } = "COP";
    public string? FiltroEstado { get; set; }
    public int? FiltroTipoId { get; set; }
    public List<Inversion> Activas => Inversiones.Where(x => x.Estado == "activa").ToList();
    public decimal TotalAportado => Activas.Sum(x => x.CapitalAportado);
    public decimal ValorPortafolio => Activas.Sum(x => x.ValorActual);
    public decimal GananciaTotal => Activas.Sum(x => x.GananciaReal);
    public decimal RentabilidadTotal => TotalAportado > 0 ? Math.Round(GananciaTotal * 100 / TotalAportado, 2) : 0;
}

public class InversionDetalleVm
{
    public Inversion Inversion { get; set; } = new();
    public List<InversionMovimiento> Movimientos { get; set; } = new();
    public List<InversionValoracion> Valoraciones { get; set; } = new();
    public List<Moneda> Monedas { get; set; } = new();
    public string MonedaBase { get; set; } = "COP";
}

public class InversionSerieVm
{
    public DateTime Fecha { get; set; }
    public decimal Valor { get; set; }
    public string Etiqueta => Fecha.ToString("MMM yyyy");
}

public class InversionesTableroVm
{
    public List<Inversion> Inversiones { get; set; } = new();
    public List<GastoCategoriaVm> Distribucion { get; set; } = new();
    public List<InversionSerieVm> Evolucion { get; set; } = new();
    public List<Inversion> ProximosEventos { get; set; } = new();
    public List<Inversion> Activas => Inversiones.Where(x => x.Estado == "activa").ToList();
    public decimal TotalAportado => Activas.Sum(x => x.CapitalAportado);
    public decimal ValorPortafolio => Activas.Sum(x => x.ValorActual);
    public decimal GananciaTotal => Activas.Sum(x => x.GananciaReal);
    public decimal RentabilidadTotal => TotalAportado > 0 ? Math.Round(GananciaTotal * 100 / TotalAportado, 2) : 0;
    public decimal LiquidezBloqueada => Activas.Where(x => x.EnPermanencia).Sum(x => x.ValorActual);
    public decimal GananciaMensualEsperada => Activas.Sum(x => x.GananciaMensualEsperada);
}

public static class CalculoInversiones
{
    public static decimal TasaMensual(decimal tasa, string periodo) =>
        periodo == "mensual"
            ? tasa
            : (decimal)(Math.Pow(1d + (double)tasa / 100d, 1d / 12d) - 1d) * 100m;

    public static decimal TasaAnual(decimal tasa, string periodo) =>
        periodo == "anual"
            ? tasa
            : (decimal)(Math.Pow(1d + (double)tasa / 100d, 12d) - 1d) * 100m;

    public static decimal Proyectar(decimal capital, decimal tasa, string periodo, DateTime desde, DateTime hasta)
    {
        if (capital <= 0 || tasa <= 0 || hasta <= desde) return capital;
        var meses = Math.Max(0, (hasta.Date - desde.Date).TotalDays / 30d);
        var tasaMensual = (double)TasaMensual(tasa, periodo) / 100d;
        return Math.Round(capital * (decimal)Math.Pow(1d + tasaMensual, meses), 0);
    }
}

// Calculo de intereses: el interes se devenga dia a dia sobre el saldo de
// capital vigente (base 30 dias = 1 mes). Cada abono a capital reduce el
// saldo y desde ese dia el interes se recalcula sobre el nuevo saldo.
public static class CalculoPrestamos
{
    public static void CompletarCalculos(Prestamo p, List<PrestamoPago> pagos, DateTime? hasta = null)
    {
        var corte = (hasta ?? DateTime.Today).Date;
        p.AbonadoCapital = pagos.Where(x => x.Tipo == "abono_capital").Sum(x => x.Monto);
        p.InteresPagado = pagos.Where(x => x.Tipo == "pago_interes").Sum(x => x.Monto);
        p.SaldoCapital = Math.Max(0, p.Capital - p.AbonadoCapital);

        decimal saldo = p.Capital, interes = 0;
        var desde = p.Fecha.Date;
        foreach (var abono in pagos.Where(x => x.Tipo == "abono_capital" && x.Fecha.Date <= corte)
                                   .OrderBy(x => x.Fecha).ThenBy(x => x.Id))
        {
            var dias = (decimal)(abono.Fecha.Date - desde).TotalDays;
            if (dias > 0) interes += saldo * (p.TasaMensual / 100m) * dias / 30m;
            saldo = Math.Max(0, saldo - abono.Monto);
            desde = abono.Fecha.Date;
        }
        var diasFinales = (decimal)(corte - desde).TotalDays;
        if (diasFinales > 0) interes += saldo * (p.TasaMensual / 100m) * diasFinales / 30m;

        p.InteresDevengado = Math.Round(interes, 0);
    }
}
