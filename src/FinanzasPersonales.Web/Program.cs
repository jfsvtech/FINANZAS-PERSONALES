using System.Globalization;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Dapper;
using FinanzasPersonales.Web.Data;
using FinanzasPersonales.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Services.AddControllersWithViews(opt =>
{
    opt.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddAntiforgery(opt =>
{
    opt.Cookie.Name = "FinanzasPersonales.AntiForgery";
    opt.Cookie.HttpOnly = true;
    opt.Cookie.SameSite = SameSiteMode.Strict;
    opt.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<AsistenteFinancieroService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddHttpClient<WhatsAppService>();
builder.Services.AddRateLimiter(opt =>
{
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opt.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
var directorioClaves = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(directorioClaves))
    directorioClaves = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(directorioClaves);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(directorioClaves))
    .SetApplicationName("FinanzasPersonales");

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opt =>
    {
        opt.LoginPath = "/Acceso/Login";
        opt.AccessDeniedPath = "/Acceso/Login";
        opt.ExpireTimeSpan = TimeSpan.FromHours(8);
        opt.SlidingExpiration = true;
        opt.Cookie.Name = "FinanzasPersonales.Auth";
        opt.Cookie.HttpOnly = true;
        opt.Cookie.SameSite = SameSiteMode.Strict;
        opt.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

var app = builder.Build();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    ForwardLimit = null
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (AntiforgeryValidationException ex) when (EsTokenAntiforgeryNoDescifrable(ex))
    {
        LimpiarCookiesProtegidas(context);
        context.Response.Redirect(context.Request.PathBase + "/Acceso/Login?sesionRestablecida=1");
    }
    catch (CryptographicException ex) when (EsLlaveDataProtectionPerdida(ex))
    {
        LimpiarCookiesProtegidas(context);
        context.Response.Redirect(context.Request.PathBase + "/Acceso/Login?sesionRestablecida=1");
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Acceso/Login");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.TryAdd("X-Content-Type-Options", "nosniff");
    headers.TryAdd("X-Frame-Options", "DENY");
    headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    headers.TryAdd("Permissions-Policy", "camera=(), geolocation=(), microphone=(self)");
    headers.TryAdd("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "font-src 'self' https://cdn.jsdelivr.net data:; " +
        "img-src 'self' data:; " +
        "connect-src 'self' https://graph.facebook.com; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'");
    await next();
});

// Cultura es-CO para formato de pesos: $ 1.234.567
var cultura = new CultureInfo("es-CO");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(cultura),
    SupportedCultures = new[] { cultura },
    SupportedUICultures = new[] { cultura }
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Inicio}/{action=Index}/{id?}");

// Crea el usuario administrador inicial si la tabla esta vacia.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Db>();
    try
    {
        using var con = db.Abrir();
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS email VARCHAR(150) NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS email_confirmado BOOLEAN NOT NULL DEFAULT FALSE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS intentos_fallidos INT NOT NULL DEFAULT 0");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS bloqueado_hasta TIMESTAMP NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS ultimo_acceso TIMESTAMP NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS valor_suscripcion NUMERIC(14,2) NOT NULL DEFAULT 0");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS ciclo_suscripcion VARCHAR(20) NOT NULL DEFAULT 'mensual'");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS fecha_inicio_suscripcion DATE NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS proximo_pago DATE NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS dias_gracia INT NOT NULL DEFAULT 3");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS estado_suscripcion VARCHAR(20) NOT NULL DEFAULT 'activa'");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS notas_suscripcion VARCHAR(500) NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS suspendido_por_mora BOOLEAN NOT NULL DEFAULT FALSE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS suspendido_en TIMESTAMP NULL");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permiso_gastos BOOLEAN NOT NULL DEFAULT TRUE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permiso_prestamos BOOLEAN NOT NULL DEFAULT TRUE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permiso_inversiones BOOLEAN NOT NULL DEFAULT TRUE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permiso_directivo BOOLEAN NOT NULL DEFAULT FALSE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permiso_asistente BOOLEAN NOT NULL DEFAULT FALSE");
        con.Execute("ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permiso_calendario BOOLEAN NOT NULL DEFAULT FALSE");
        con.Execute(
            @"UPDATE usuarios SET permiso_gastos=TRUE, permiso_prestamos=TRUE, permiso_inversiones=TRUE,
                     permiso_directivo=TRUE, permiso_asistente=TRUE, permiso_calendario=TRUE
              WHERE es_admin=TRUE");
        con.Execute(
            @"UPDATE usuarios
              SET email = CASE
                  WHEN email IS NOT NULL AND email <> '' THEN LOWER(email)
                  WHEN POSITION('@' IN nombre_usuario) > 1 THEN LOWER(nombre_usuario)
                  ELSE LOWER(nombre_usuario || '@local.local')
              END,
              email_confirmado = TRUE
              WHERE email IS NULL OR email = '' OR email_confirmado = FALSE");
        con.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_usuarios_email_lower ON usuarios (LOWER(email))");
        con.Execute(
            @"CREATE TABLE IF NOT EXISTS usuario_tokens (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE,
                  tipo VARCHAR(30) NOT NULL CHECK (tipo IN ('recuperar_clave','confirmar_email')),
                  token_hash VARCHAR(128) NOT NULL UNIQUE,
                  email_destino VARCHAR(150) NOT NULL,
                  expira_en TIMESTAMP NOT NULL,
                  usado_en TIMESTAMP NULL,
                  creado_en TIMESTAMP NOT NULL DEFAULT NOW(),
                  creado_ip VARCHAR(64) NULL
              );
              CREATE INDEX IF NOT EXISTS idx_usuario_tokens_usuario_tipo ON usuario_tokens (usuario_id, tipo, expira_en)");
        con.Execute(
            @"CREATE TABLE IF NOT EXISTS usuario_pagos_suscripcion (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE,
                  fecha_pago DATE NOT NULL,
                  monto NUMERIC(14,2) NOT NULL CHECK (monto > 0),
                  periodo_cubierto VARCHAR(80) NOT NULL,
                  metodo VARCHAR(60) NULL,
                  referencia VARCHAR(100) NULL,
                  notas VARCHAR(300) NULL,
                  creado_en TIMESTAMP NOT NULL DEFAULT NOW()
              );
              CREATE INDEX IF NOT EXISTS idx_usuario_pagos_suscripcion_usuario_fecha
              ON usuario_pagos_suscripcion (usuario_id, fecha_pago DESC)");
        con.Execute(
            @"CREATE TABLE IF NOT EXISTS configuraciones_sistema (
                  clave VARCHAR(100) PRIMARY KEY,
                  valor TEXT NULL,
                  protegido BOOLEAN NOT NULL DEFAULT FALSE,
                  actualizado_en TIMESTAMP NOT NULL DEFAULT NOW()
              )");
        con.Execute(
            @"CREATE TABLE IF NOT EXISTS configuraciones_usuario (
                  usuario_id INT PRIMARY KEY REFERENCES usuarios(id),
                  incluir_saldo_anterior BOOLEAN NOT NULL DEFAULT FALSE
              )");
        con.Execute(
            @"DO $$
              BEGIN
                IF to_regclass('public.gastos_periodicos') IS NOT NULL THEN
                  ALTER TABLE gastos_periodicos ADD COLUMN IF NOT EXISTS tipo VARCHAR(10) NOT NULL DEFAULT 'gasto';
                  UPDATE gastos_periodicos SET tipo='gasto' WHERE tipo IS NULL OR tipo NOT IN ('gasto','ingreso');
                  IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE conname = 'ck_gastos_periodicos_tipo'
                  ) THEN
                    ALTER TABLE gastos_periodicos
                    ADD CONSTRAINT ck_gastos_periodicos_tipo CHECK (tipo IN ('gasto','ingreso'));
                  END IF;
                END IF;
              END $$");
        con.Execute("ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS icono VARCHAR(50) NOT NULL DEFAULT 'bi-bank'");
        con.Execute("ALTER TABLE categorias ADD COLUMN IF NOT EXISTS icono VARCHAR(50) NOT NULL DEFAULT 'bi-tag'");
        con.Execute(
            @"CREATE TABLE IF NOT EXISTS metas_ahorro (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id),
                  nombre VARCHAR(100) NOT NULL,
                  monto_objetivo NUMERIC(14,2) NOT NULL CHECK (monto_objetivo > 0),
                  fecha_objetivo DATE NULL,
                  color VARCHAR(7) NOT NULL DEFAULT '#6f42c1',
                  activo BOOLEAN NOT NULL DEFAULT TRUE,
                  creado_en TIMESTAMP NOT NULL DEFAULT NOW()
              );
              CREATE TABLE IF NOT EXISTS aportes_meta (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id),
                  meta_ahorro_id INT NOT NULL REFERENCES metas_ahorro(id),
                  fecha DATE NOT NULL,
                  monto NUMERIC(14,2) NOT NULL CHECK (monto > 0),
                  notas VARCHAR(200) NULL
              )");
        con.Execute(
            @"CREATE TABLE IF NOT EXISTS tipos_inversion (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id),
                  nombre VARCHAR(80) NOT NULL,
                  color VARCHAR(7) NOT NULL DEFAULT '#D4AF37',
                  icono VARCHAR(50) NOT NULL DEFAULT 'bi-graph-up-arrow',
                  activo BOOLEAN NOT NULL DEFAULT TRUE,
                  UNIQUE(usuario_id,nombre)
              );
              CREATE TABLE IF NOT EXISTS inversiones (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id),
                  nombre VARCHAR(100) NOT NULL,
                  entidad VARCHAR(100) NULL,
                  tipo VARCHAR(80) NOT NULL,
                  tipo_inversion_id INT NULL REFERENCES tipos_inversion(id),
                  fecha_inicio DATE NOT NULL,
                  capital_inicial NUMERIC(16,2) NOT NULL CHECK (capital_inicial > 0),
                  tasa NUMERIC(9,4) NOT NULL DEFAULT 0 CHECK (tasa >= 0),
                  periodo_tasa VARCHAR(10) NOT NULL DEFAULT 'anual' CHECK (periodo_tasa IN ('mensual','anual')),
                  tipo_rendimiento VARCHAR(10) NOT NULL DEFAULT 'fijo' CHECK (tipo_rendimiento IN ('fijo','variable')),
                  fecha_retorno DATE NULL,
                  permanencia_meses INT NOT NULL DEFAULT 0 CHECK (permanencia_meses BETWEEN 0 AND 600),
                  penalidad_retiro NUMERIC(7,3) NULL CHECK (penalidad_retiro BETWEEN 0 AND 100),
                  renovacion_automatica BOOLEAN NOT NULL DEFAULT FALSE,
                  moneda VARCHAR(5) NOT NULL DEFAULT 'COP',
                  color VARCHAR(7) NOT NULL DEFAULT '#D4AF37',
                  icono VARCHAR(50) NOT NULL DEFAULT 'bi-graph-up-arrow',
                  notas VARCHAR(500) NULL,
                  estado VARCHAR(12) NOT NULL DEFAULT 'activa' CHECK (estado IN ('activa','cerrada')),
                  creado_en TIMESTAMP NOT NULL DEFAULT NOW()
              );
              CREATE TABLE IF NOT EXISTS inversion_movimientos (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id),
                  inversion_id INT NOT NULL REFERENCES inversiones(id) ON DELETE CASCADE,
                  fecha DATE NOT NULL,
                  tipo VARCHAR(15) NOT NULL CHECK (tipo IN ('aporte','retiro','rendimiento','costo')),
                  monto NUMERIC(16,2) NOT NULL CHECK (monto > 0),
                  notas VARCHAR(250) NULL,
                  creado_en TIMESTAMP NOT NULL DEFAULT NOW()
              );
              CREATE TABLE IF NOT EXISTS inversion_valoraciones (
                  id SERIAL PRIMARY KEY,
                  usuario_id INT NOT NULL REFERENCES usuarios(id),
                  inversion_id INT NOT NULL REFERENCES inversiones(id) ON DELETE CASCADE,
                  fecha DATE NOT NULL,
                  valor NUMERIC(16,2) NOT NULL CHECK (valor >= 0),
                  notas VARCHAR(250) NULL,
                  creado_en TIMESTAMP NOT NULL DEFAULT NOW()
              );
              CREATE INDEX IF NOT EXISTS idx_inversiones_usuario ON inversiones (usuario_id);
              CREATE INDEX IF NOT EXISTS idx_tipos_inversion_usuario ON tipos_inversion (usuario_id);
              CREATE INDEX IF NOT EXISTS idx_inversion_movimientos_inversion ON inversion_movimientos (inversion_id, fecha);
              CREATE INDEX IF NOT EXISTS idx_inversion_valoraciones_inversion ON inversion_valoraciones (inversion_id, fecha)");
        con.Execute("ALTER TABLE inversiones ADD COLUMN IF NOT EXISTS tipo_inversion_id INT NULL REFERENCES tipos_inversion(id)");
        con.Execute("ALTER TABLE inversiones ALTER COLUMN tipo TYPE VARCHAR(80)");
        con.Execute(
            @"INSERT INTO tipos_inversion(usuario_id,nombre,color,icono)
              SELECT u.id,v.nombre,v.color,v.icono FROM usuarios u
              CROSS JOIN (VALUES
                ('CDT / deposito a termino','#D4AF37','bi-bank'),
                ('Fondo de inversion','#7C3AED','bi-pie-chart'),
                ('Acciones','#2F9E64','bi-bar-chart'),
                ('ETF','#4C6EF5','bi-globe2'),
                ('Criptoactivo','#F59F00','bi-currency-bitcoin'),
                ('Participacion en negocio','#A78BFA','bi-briefcase'),
                ('Inmueble','#E8590C','bi-building'),
                ('Otra inversion','#6B7280','bi-graph-up-arrow')
              ) AS v(nombre,color,icono)
              WHERE NOT EXISTS (
                SELECT 1 FROM tipos_inversion ti
                WHERE ti.usuario_id=u.id AND LOWER(ti.nombre)=LOWER(v.nombre)
              )");
        con.Execute(
            @"UPDATE inversiones i SET tipo_inversion_id=ti.id
              FROM tipos_inversion ti
              WHERE i.tipo_inversion_id IS NULL AND ti.usuario_id=i.usuario_id
              AND LOWER(ti.nombre)=LOWER(CASE i.tipo
                WHEN 'cdt' THEN 'CDT / deposito a termino'
                WHEN 'fondo' THEN 'Fondo de inversion'
                WHEN 'acciones' THEN 'Acciones'
                WHEN 'etf' THEN 'ETF'
                WHEN 'cripto' THEN 'Criptoactivo'
                WHEN 'negocio' THEN 'Participacion en negocio'
                WHEN 'inmueble' THEN 'Inmueble'
                ELSE 'Otra inversion' END)");
        con.Execute(
            @"DO $$
              BEGIN
                IF to_regclass('public.usuarios') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_usuarios_activo_suscripcion ON usuarios (activo, estado_suscripcion, proximo_pago);
                  CREATE INDEX IF NOT EXISTS idx_usuarios_bloqueado_hasta ON usuarios (bloqueado_hasta) WHERE bloqueado_hasta IS NOT NULL;
                END IF;
                IF to_regclass('public.usuario_tokens') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_usuario_tokens_hash_vigente ON usuario_tokens (token_hash, tipo, expira_en) WHERE usado_en IS NULL;
                END IF;
                IF to_regclass('public.movimientos') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_tipo_fecha ON movimientos (usuario_id, tipo, fecha DESC);
                  CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_categoria_fecha ON movimientos (usuario_id, categoria_id, fecha DESC);
                  CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_cuenta_fecha ON movimientos (usuario_id, cuenta_id, fecha DESC);
                  CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_cuenta_destino_fecha ON movimientos (usuario_id, cuenta_destino_id, fecha DESC) WHERE cuenta_destino_id IS NOT NULL;
                  CREATE INDEX IF NOT EXISTS idx_movimientos_gasto_periodico ON movimientos (gasto_periodico_id) WHERE gasto_periodico_id IS NOT NULL;
                END IF;
                IF to_regclass('public.cuentas') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_cuentas_usuario_activo_tipo ON cuentas (usuario_id, activo, tipo, nombre);
                END IF;
                IF to_regclass('public.categorias') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_categorias_usuario_activo_tipo ON categorias (usuario_id, activo, tipo, nombre);
                END IF;
                IF to_regclass('public.gastos_periodicos') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_gastos_periodicos_usuario_activo_fecha ON gastos_periodicos (usuario_id, activo, proxima_fecha);
                  CREATE INDEX IF NOT EXISTS idx_gastos_periodicos_usuario_tipo_fecha ON gastos_periodicos (usuario_id, tipo, proxima_fecha);
                END IF;
                IF to_regclass('public.presupuestos') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_presupuestos_usuario_categoria ON presupuestos (usuario_id, categoria_id);
                END IF;
                IF to_regclass('public.personas') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_personas_usuario_activo_nombre ON personas (usuario_id, activo, nombre);
                END IF;
                IF to_regclass('public.prestamos') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_estado_fecha ON prestamos (usuario_id, estado, fecha DESC);
                  CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_persona_estado ON prestamos (usuario_id, persona_id, estado);
                  CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_fecha_pago_capital ON prestamos (usuario_id, fecha_pago_capital) WHERE fecha_pago_capital IS NOT NULL;
                END IF;
                IF to_regclass('public.prestamo_pagos') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_usuario_prestamo_fecha ON prestamo_pagos (usuario_id, prestamo_id, fecha DESC);
                  CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_usuario_tipo_fecha ON prestamo_pagos (usuario_id, tipo, fecha DESC);
                END IF;
                IF to_regclass('public.metas_ahorro') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_metas_usuario_activo_fecha ON metas_ahorro (usuario_id, activo, fecha_objetivo);
                END IF;
                IF to_regclass('public.aportes_meta') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_aportes_meta_usuario_meta_fecha ON aportes_meta (usuario_id, meta_ahorro_id, fecha DESC);
                END IF;
                IF to_regclass('public.inversiones') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_inversiones_usuario_estado_tipo ON inversiones (usuario_id, estado, tipo_inversion_id);
                  CREATE INDEX IF NOT EXISTS idx_inversiones_usuario_retorno ON inversiones (usuario_id, estado, fecha_retorno) WHERE fecha_retorno IS NOT NULL;
                END IF;
                IF to_regclass('public.inversion_movimientos') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_inversion_movimientos_usuario_inversion_fecha ON inversion_movimientos (usuario_id, inversion_id, fecha DESC);
                END IF;
                IF to_regclass('public.inversion_valoraciones') IS NOT NULL THEN
                  CREATE INDEX IF NOT EXISTS idx_inversion_valoraciones_usuario_inversion_fecha ON inversion_valoraciones (usuario_id, inversion_id, fecha DESC);
                END IF;
              END $$;");
        var hayUsuarios = con.ExecuteScalar<int>("SELECT COUNT(*) FROM usuarios");
        if (hayUsuarios == 0)
        {
            var adminEmail = builder.Configuration["InitialAdmin:Email"] ?? "admin@local.local";
            var adminPassword = builder.Configuration["InitialAdmin:Password"] ?? CrearPasswordInicial();
            var hash = BCrypt.Net.BCrypt.HashPassword(adminPassword);
            con.Execute(
                @"INSERT INTO usuarios (nombre_usuario, email, nombre_completo, clave_hash, es_admin, email_confirmado,
                       permiso_gastos, permiso_prestamos, permiso_inversiones, permiso_directivo, permiso_asistente, permiso_calendario)
                  VALUES ('admin', @adminEmail, 'Administrador', @hash, TRUE, TRUE,
                       TRUE, TRUE, TRUE, TRUE, TRUE, TRUE)", new { hash, adminEmail });
            Console.WriteLine($"Usuario inicial creado -> correo: {adminEmail}  clave: {adminPassword} (cambiala al entrar)");
        }
        con.Execute(
            @"INSERT INTO tipos_inversion(usuario_id,nombre,color,icono)
              SELECT u.id,v.nombre,v.color,v.icono FROM usuarios u
              CROSS JOIN (VALUES
                ('CDT / deposito a termino','#D4AF37','bi-bank'),
                ('Fondo de inversion','#7C3AED','bi-pie-chart'),
                ('Acciones','#2F9E64','bi-bar-chart'),
                ('ETF','#4C6EF5','bi-globe2'),
                ('Criptoactivo','#F59F00','bi-currency-bitcoin'),
                ('Participacion en negocio','#A78BFA','bi-briefcase'),
                ('Inmueble','#E8590C','bi-building'),
                ('Otra inversion','#6B7280','bi-graph-up-arrow')
              ) AS v(nombre,color,icono)
              WHERE NOT EXISTS (
                SELECT 1 FROM tipos_inversion ti
                WHERE ti.usuario_id=u.id AND LOWER(ti.nombre)=LOWER(v.nombre)
              )");
    }
    catch (Exception ex)
    {
        Console.WriteLine("AVISO: no se pudo verificar/crear el usuario inicial. " +
                          "Revisa la cadena de conexion y que el esquema sql/schema.sql este aplicado. Detalle: " + ex.Message);
    }
}

app.Run();

static bool EsTokenAntiforgeryNoDescifrable(Exception ex)
{
    return ex.Message.Contains("could not be decrypted", StringComparison.OrdinalIgnoreCase) ||
           EsLlaveDataProtectionPerdida(ex) ||
           (ex.InnerException != null && EsLlaveDataProtectionPerdida(ex.InnerException));
}

static bool EsLlaveDataProtectionPerdida(Exception ex)
{
    return ex.Message.Contains("was not found in the key ring", StringComparison.OrdinalIgnoreCase) ||
           ex.Message.Contains("key ring", StringComparison.OrdinalIgnoreCase);
}

static void LimpiarCookiesProtegidas(HttpContext context)
{
    var opciones = new CookieOptions
    {
        HttpOnly = true,
        Secure = !context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/"
    };

    context.Response.Cookies.Delete("FinanzasPersonales.Auth", opciones);
    context.Response.Cookies.Delete("FinanzasPersonales.AntiForgery", opciones);

    foreach (var cookie in context.Request.Cookies.Keys)
    {
        if (cookie.StartsWith(".AspNetCore.Antiforgery", StringComparison.OrdinalIgnoreCase) ||
            cookie.StartsWith("FinanzasPersonales.", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Cookies.Delete(cookie, opciones);
        }
    }
}

static string CrearPasswordInicial()
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789@$!%*?&";
    var bytes = RandomNumberGenerator.GetBytes(24);
    return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
}
