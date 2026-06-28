using System.Globalization;
using System.Net;
using System.Net.Mail;
using Dapper;
using FinanzasPersonales.Web.Data;

namespace FinanzasPersonales.Web.Services;

public class RecordatoriosEmailHostedService : BackgroundService
{
    private const string ClaveActivo = "RecordatoriosEmail:Activo";
    private const string ClaveDiasAntes = "RecordatoriosEmail:DiasAntes";
    private static readonly TimeSpan IntervaloRevision = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly ILogger<RecordatoriosEmailHostedService> _logger;

    public RecordatoriosEmailHostedService(IServiceProvider services, ILogger<RecordatoriosEmailHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcesarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ejecutando recordatorios automaticos por correo.");
            }

            await Task.Delay(IntervaloRevision, stoppingToken);
        }
    }

    private async Task ProcesarAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Db>();
        var email = scope.ServiceProvider.GetRequiredService<EmailService>();

        using var con = db.Abrir();
        var activo = LeerBool(con.ExecuteScalar<string?>(
            "SELECT valor FROM configuraciones_sistema WHERE clave=@clave",
            new { clave = ClaveActivo }), true);
        if (!activo)
        {
            _logger.LogInformation("Recordatorios automaticos por correo desactivados.");
            return;
        }

        if (!email.Configurado)
        {
            _logger.LogWarning("Recordatorios automaticos omitidos: correo no configurado.");
            return;
        }

        var diasAntes = LeerInt(con.ExecuteScalar<string?>(
            "SELECT valor FROM configuraciones_sistema WHERE clave=@clave",
            new { clave = ClaveDiasAntes }), 3, 0, 60);
        var hoy = DateTime.Today;
        var hasta = hoy.AddDays(diasAntes);

        var enviados = 0;
        foreach (var recordatorio in ObtenerRecordatoriosPeriodicos(con, hoy, hasta)
                     .Concat(ObtenerRecordatoriosPrestamos(con, hoy, hasta)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!EmailValido(recordatorio.Destinatario))
                continue;
            if (!ReservarEnvio(con, recordatorio))
                continue;

            try
            {
                var ok = await email.EnviarAsync(recordatorio.Destinatario, recordatorio.Asunto, recordatorio.Html);
                if (!ok)
                {
                    LiberarReserva(con, recordatorio);
                    continue;
                }

                enviados++;
            }
            catch (Exception ex)
            {
                LiberarReserva(con, recordatorio);
                _logger.LogError(ex,
                    "No se pudo enviar recordatorio {Tipo} entidad {EntidadId} a {Destinatario}.",
                    recordatorio.Tipo, recordatorio.EntidadId, recordatorio.Destinatario);
            }
        }

        if (enviados > 0)
            _logger.LogInformation("Recordatorios automaticos enviados: {Cantidad}.", enviados);
    }

    private static IEnumerable<RecordatorioEmailPendiente> ObtenerRecordatoriosPeriodicos(
        System.Data.IDbConnection con, DateTime hoy, DateTime hasta)
    {
        var items = con.Query<RecordatorioPeriodicoDb>(
            @"SELECT gp.id AS Id, gp.usuario_id AS UsuarioId, gp.tipo, gp.nombre,
                     gp.monto_estimado AS Monto, gp.proxima_fecha AS FechaEvento,
                     COALESCE(gp.moneda_codigo, u.moneda_codigo, 'COP') AS MonedaCodigo,
                     u.email AS UsuarioEmail, u.nombre_completo AS UsuarioNombre
              FROM gastos_periodicos gp
              JOIN usuarios u ON u.id=gp.usuario_id
              WHERE gp.activo=TRUE
                AND u.activo=TRUE
                AND gp.proxima_fecha BETWEEN @hoy AND @hasta",
            new { hoy, hasta });

        foreach (var item in items)
        {
            var tipoTexto = item.Tipo == "ingreso" ? "ingreso recurrente" : "gasto recurrente";
            var asunto = $"Recordatorio: {item.Nombre} vence el {item.FechaEvento:dd/MM/yyyy}";
            var detalle = $"Tu {tipoTexto} esta programado para el {item.FechaEvento:dd/MM/yyyy}.";
            yield return new RecordatorioEmailPendiente(
                UsuarioId: item.UsuarioId,
                Tipo: $"periodico-{item.Tipo}",
                EntidadId: item.Id,
                FechaEvento: item.FechaEvento.Date,
                Destinatario: item.UsuarioEmail,
                Asunto: asunto,
                Html: CrearHtml("Recordatorio financiero", item.Nombre, detalle, item.Monto, item.MonedaCodigo, "Gestion de Finanzas"));
        }
    }

    private static IEnumerable<RecordatorioEmailPendiente> ObtenerRecordatoriosPrestamos(
        System.Data.IDbConnection con, DateTime hoy, DateTime hasta)
    {
        var prestamos = con.Query<RecordatorioPrestamoDb>(
            @"SELECT p.id AS Id, p.usuario_id AS UsuarioId, p.persona_id AS PersonaId,
                     p.capital, p.tasa_mensual AS TasaMensual,
                     p.dia_pago_interes AS DiaPagoInteres, p.fecha_pago_capital AS FechaPagoCapital,
                     COALESCE(p.moneda_codigo, u.moneda_codigo, 'COP') AS MonedaCodigo,
                     pe.nombre AS PersonaNombre, pe.email AS PersonaEmail,
                     u.email AS UsuarioEmail, u.nombre_completo AS UsuarioNombre,
                     GREATEST(0, p.capital - COALESCE((
                         SELECT SUM(pp.monto) FROM prestamo_pagos pp
                         WHERE pp.prestamo_id=p.id AND pp.usuario_id=p.usuario_id AND pp.tipo='abono_capital'
                     ), 0)) AS SaldoCapital
              FROM prestamos p
              JOIN personas pe ON pe.id=p.persona_id
              JOIN usuarios u ON u.id=p.usuario_id
              WHERE p.estado='activo'
                AND u.activo=TRUE
                AND GREATEST(0, p.capital - COALESCE((
                    SELECT SUM(pp.monto) FROM prestamo_pagos pp
                    WHERE pp.prestamo_id=p.id AND pp.usuario_id=p.usuario_id AND pp.tipo='abono_capital'
                ), 0)) > 0");

        foreach (var prestamo in prestamos)
        {
            if (prestamo.TasaMensual > 0 && prestamo.DiaPagoInteres.HasValue)
            {
                var fechaInteres = ProximaFechaPorDia(prestamo.DiaPagoInteres.Value, hoy);
                if (fechaInteres >= hoy && fechaInteres <= hasta)
                {
                    var interes = Math.Round(prestamo.SaldoCapital * prestamo.TasaMensual / 100m, 0);
                    foreach (var recordatorio in CrearRecordatoriosPrestamo(prestamo, fechaInteres, interes,
                                 "interes", "pago de intereses"))
                        yield return recordatorio;
                }
            }

            if (prestamo.FechaPagoCapital.HasValue)
            {
                var fechaCapital = prestamo.FechaPagoCapital.Value.Date;
                if (fechaCapital >= hoy && fechaCapital <= hasta)
                {
                    foreach (var recordatorio in CrearRecordatoriosPrestamo(prestamo, fechaCapital, prestamo.SaldoCapital,
                                 "capital", "pago final del prestamo"))
                        yield return recordatorio;
                }
            }
        }
    }

    private static IEnumerable<RecordatorioEmailPendiente> CrearRecordatoriosPrestamo(
        RecordatorioPrestamoDb prestamo, DateTime fechaEvento, decimal monto, string tipoEvento, string concepto)
    {
        var asuntoDueno = $"Recordatorio de cobro: {prestamo.PersonaNombre} - {fechaEvento:dd/MM/yyyy}";
        var detalleDueno = $"Debes cobrar a {prestamo.PersonaNombre} el {concepto} programado para el {fechaEvento:dd/MM/yyyy}.";
        yield return new RecordatorioEmailPendiente(
            prestamo.UsuarioId,
            $"prestamo-{tipoEvento}-dueno",
            prestamo.Id,
            fechaEvento.Date,
            prestamo.UsuarioEmail,
            asuntoDueno,
            CrearHtml("Recordatorio de cobro", prestamo.PersonaNombre, detalleDueno, monto, prestamo.MonedaCodigo, "Gestion de Prestamos"));

        if (EmailValido(prestamo.PersonaEmail))
        {
            var asuntoCliente = $"Recordatorio de pago de prestamo - {fechaEvento:dd/MM/yyyy}";
            var detalleCliente = $"Te recordamos que tienes un {concepto} programado para el {fechaEvento:dd/MM/yyyy}.";
            yield return new RecordatorioEmailPendiente(
                prestamo.UsuarioId,
                $"prestamo-{tipoEvento}-cliente",
                prestamo.Id,
                fechaEvento.Date,
                prestamo.PersonaEmail!,
                asuntoCliente,
                CrearHtml("Recordatorio de pago", prestamo.PersonaNombre, detalleCliente, monto, prestamo.MonedaCodigo, "Finanzas Personales"));
        }
    }

    private static DateTime ProximaFechaPorDia(int dia, DateTime desde)
    {
        var diaMes = Math.Min(dia, DateTime.DaysInMonth(desde.Year, desde.Month));
        var fecha = new DateTime(desde.Year, desde.Month, diaMes);
        if (fecha.Date < desde.Date)
        {
            var siguiente = desde.AddMonths(1);
            fecha = new DateTime(siguiente.Year, siguiente.Month,
                Math.Min(dia, DateTime.DaysInMonth(siguiente.Year, siguiente.Month)));
        }
        return fecha;
    }

    private static bool ReservarEnvio(System.Data.IDbConnection con, RecordatorioEmailPendiente recordatorio)
    {
        return con.ExecuteScalar<int>(
            @"INSERT INTO recordatorios_email_enviados
                (usuario_id, tipo, entidad_id, fecha_evento, destinatario)
              VALUES (@UsuarioId, @Tipo, @EntidadId, @FechaEvento, @Destinatario)
              ON CONFLICT (usuario_id, tipo, entidad_id, fecha_evento, destinatario) DO NOTHING
              RETURNING 1",
            recordatorio) == 1;
    }

    private static void LiberarReserva(System.Data.IDbConnection con, RecordatorioEmailPendiente recordatorio)
    {
        con.Execute(
            @"DELETE FROM recordatorios_email_enviados
              WHERE usuario_id=@UsuarioId AND tipo=@Tipo AND entidad_id=@EntidadId
                AND fecha_evento=@FechaEvento AND destinatario=@Destinatario",
            recordatorio);
    }

    private static string CrearHtml(string etiqueta, string titulo, string detalle, decimal monto, string moneda, string modulo)
    {
        var valor = monto.ToString("N0", new CultureInfo("es-CO"));
        return $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;padding:24px;background:#F5F5F7;color:#1C1C1E">
              <div style="background:#fff;border:1px solid #E5E7EB;border-radius:18px;padding:28px;box-shadow:0 12px 30px rgba(17,24,39,.08)">
                <div style="color:#7C3AED;font-weight:800;letter-spacing:.08em;text-transform:uppercase;font-size:12px">{WebUtility.HtmlEncode(etiqueta)}</div>
                <h2 style="margin:8px 0 10px;color:#1C1C1E">{WebUtility.HtmlEncode(titulo)}</h2>
                <p style="color:#4B5563;font-size:15px;line-height:1.55">{WebUtility.HtmlEncode(detalle)}</p>
                <div style="margin:22px 0;padding:18px;border-radius:14px;background:#FFF8E1;border:1px solid #F2C94C">
                  <div style="font-size:12px;color:#6B7280;text-transform:uppercase;letter-spacing:.08em">Monto de referencia</div>
                  <div style="font-size:28px;font-weight:800;color:#7C3AED">{WebUtility.HtmlEncode(moneda)} {valor}</div>
                </div>
                <p style="margin:0;color:#6B7280;font-size:13px">Modulo: {WebUtility.HtmlEncode(modulo)}. Este mensaje fue generado automaticamente.</p>
              </div>
            </div>
            """;
    }

    private static bool EmailValido(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;
        try
        {
            _ = new MailAddress(email.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LeerBool(string? valor, bool defecto)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return defecto;
        return bool.TryParse(valor, out var parsed) ? parsed : valor == "1";
    }

    private static int LeerInt(string? valor, int defecto, int minimo, int maximo)
    {
        if (!int.TryParse(valor, out var parsed))
            return defecto;
        return Math.Clamp(parsed, minimo, maximo);
    }

    private sealed record RecordatorioEmailPendiente(
        int UsuarioId,
        string Tipo,
        int EntidadId,
        DateTime FechaEvento,
        string Destinatario,
        string Asunto,
        string Html);

    private sealed class RecordatorioPeriodicoDb
    {
        public int Id { get; set; }
        public int UsuarioId { get; set; }
        public string Tipo { get; set; } = "gasto";
        public string Nombre { get; set; } = "";
        public decimal Monto { get; set; }
        public DateTime FechaEvento { get; set; }
        public string MonedaCodigo { get; set; } = "COP";
        public string UsuarioEmail { get; set; } = "";
        public string UsuarioNombre { get; set; } = "";
    }

    private sealed class RecordatorioPrestamoDb
    {
        public int Id { get; set; }
        public int UsuarioId { get; set; }
        public int PersonaId { get; set; }
        public decimal Capital { get; set; }
        public decimal TasaMensual { get; set; }
        public int? DiaPagoInteres { get; set; }
        public DateTime? FechaPagoCapital { get; set; }
        public string MonedaCodigo { get; set; } = "COP";
        public string PersonaNombre { get; set; } = "";
        public string? PersonaEmail { get; set; }
        public string UsuarioEmail { get; set; } = "";
        public string UsuarioNombre { get; set; } = "";
        public decimal SaldoCapital { get; set; }
    }
}
