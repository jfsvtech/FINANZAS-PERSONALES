-- ============================================================
-- FinanzasPersonales - Esquema PostgreSQL
-- Crear la base de datos antes de ejecutar este script:
--   CREATE DATABASE finanzas_personales;
-- Luego ejecutar este archivo conectado a esa base de datos.
-- ============================================================

CREATE TABLE IF NOT EXISTS usuarios (
    id              SERIAL PRIMARY KEY,
    nombre_usuario  VARCHAR(50)  NOT NULL UNIQUE,
    email           VARCHAR(150) NOT NULL UNIQUE,
    nombre_completo VARCHAR(100) NOT NULL,
    clave_hash      VARCHAR(100) NOT NULL,
    es_admin        BOOLEAN      NOT NULL DEFAULT FALSE,
    activo          BOOLEAN      NOT NULL DEFAULT TRUE,
    email_confirmado BOOLEAN     NOT NULL DEFAULT FALSE,
    intentos_fallidos INT        NOT NULL DEFAULT 0,
    bloqueado_hasta TIMESTAMP    NULL,
    ultimo_acceso   TIMESTAMP    NULL,
    valor_suscripcion NUMERIC(14,2) NOT NULL DEFAULT 0,
    ciclo_suscripcion VARCHAR(20) NOT NULL DEFAULT 'mensual',
    fecha_inicio_suscripcion DATE NULL,
    proximo_pago DATE NULL,
    dias_gracia INT NOT NULL DEFAULT 3,
    estado_suscripcion VARCHAR(20) NOT NULL DEFAULT 'activa',
    notas_suscripcion VARCHAR(500) NULL,
    suspendido_por_mora BOOLEAN NOT NULL DEFAULT FALSE,
    suspendido_en TIMESTAMP NULL,
    permiso_gastos BOOLEAN NOT NULL DEFAULT TRUE,
    permiso_prestamos BOOLEAN NOT NULL DEFAULT TRUE,
    permiso_inversiones BOOLEAN NOT NULL DEFAULT TRUE,
    permiso_directivo BOOLEAN NOT NULL DEFAULT FALSE,
    permiso_asistente BOOLEAN NOT NULL DEFAULT FALSE,
    permiso_calendario BOOLEAN NOT NULL DEFAULT FALSE,
    creado_en       TIMESTAMP    NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS usuario_tokens (
    id            SERIAL PRIMARY KEY,
    usuario_id    INT          NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE,
    tipo          VARCHAR(30)  NOT NULL CHECK (tipo IN ('recuperar_clave','confirmar_email')),
    token_hash    VARCHAR(128) NOT NULL UNIQUE,
    email_destino VARCHAR(150) NOT NULL,
    expira_en     TIMESTAMP    NOT NULL,
    usado_en      TIMESTAMP    NULL,
    creado_en     TIMESTAMP    NOT NULL DEFAULT NOW(),
    creado_ip     VARCHAR(64)  NULL
);

CREATE TABLE IF NOT EXISTS usuario_pagos_suscripcion (
    id               SERIAL PRIMARY KEY,
    usuario_id       INT           NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE,
    fecha_pago       DATE          NOT NULL,
    monto            NUMERIC(14,2) NOT NULL CHECK (monto > 0),
    periodo_cubierto VARCHAR(80)   NOT NULL,
    metodo           VARCHAR(60)   NULL,
    referencia       VARCHAR(100)  NULL,
    notas            VARCHAR(300)  NULL,
    creado_en        TIMESTAMP     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_usuario_pagos_suscripcion_usuario_fecha
ON usuario_pagos_suscripcion (usuario_id, fecha_pago DESC);

CREATE TABLE IF NOT EXISTS configuraciones_sistema (
    clave          VARCHAR(100) PRIMARY KEY,
    valor          TEXT NULL,
    protegido      BOOLEAN NOT NULL DEFAULT FALSE,
    actualizado_en TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Cuentas / medios de pago del usuario.
-- tipo: 'efectivo' | 'debito' | 'tarjeta_credito'
CREATE TABLE IF NOT EXISTS cuentas (
    id         SERIAL PRIMARY KEY,
    usuario_id INT          NOT NULL REFERENCES usuarios(id),
    nombre     VARCHAR(80)  NOT NULL,
    tipo       VARCHAR(20)  NOT NULL CHECK (tipo IN ('efectivo','debito','tarjeta_credito')),
    icono      VARCHAR(50)  NOT NULL DEFAULT 'bi-bank',
    dia_pago   INT          NULL CHECK (dia_pago BETWEEN 1 AND 31), -- solo tarjetas: dia habitual de pago
    activo     BOOLEAN      NOT NULL DEFAULT TRUE
);

-- Categorias de ingreso o gasto.
-- clase (solo aplica a gastos): 'fijo' | 'variable' | 'periodico'
CREATE TABLE IF NOT EXISTS categorias (
    id         SERIAL PRIMARY KEY,
    usuario_id INT         NOT NULL REFERENCES usuarios(id),
    nombre     VARCHAR(80) NOT NULL,
    tipo       VARCHAR(10) NOT NULL CHECK (tipo IN ('ingreso','gasto')),
    clase      VARCHAR(10) NOT NULL DEFAULT 'variable' CHECK (clase IN ('fijo','variable','periodico')),
    color      VARCHAR(7)  NOT NULL DEFAULT '#6c757d',
    icono      VARCHAR(50) NOT NULL DEFAULT 'bi-tag',
    activo     BOOLEAN     NOT NULL DEFAULT TRUE
);

-- Movimientos recurrentes (cada N meses) con recordatorio.
CREATE TABLE IF NOT EXISTS gastos_periodicos (
    id               SERIAL PRIMARY KEY,
    usuario_id       INT           NOT NULL REFERENCES usuarios(id),
    tipo             VARCHAR(10)   NOT NULL DEFAULT 'gasto' CHECK (tipo IN ('gasto','ingreso')),
    nombre           VARCHAR(100)  NOT NULL,
    categoria_id     INT           NOT NULL REFERENCES categorias(id),
    cuenta_id        INT           NULL REFERENCES cuentas(id),
    monto_estimado   NUMERIC(14,2) NOT NULL CHECK (monto_estimado > 0),
    frecuencia_meses INT           NOT NULL DEFAULT 1 CHECK (frecuencia_meses BETWEEN 1 AND 60),
    proxima_fecha    DATE          NOT NULL,
    activo           BOOLEAN       NOT NULL DEFAULT TRUE
);

-- Movimientos: el corazon del sistema.
-- tipo:
--   'ingreso'      -> entra dinero a cuenta_id (efectivo o debito)
--   'gasto'        -> sale dinero de cuenta_id (cualquier tipo).
--                     Si cuenta_id es tarjeta_credito, el gasto cuenta en el mes
--                     de la compra y aumenta la deuda de la tarjeta.
--   'pago_tarjeta' -> transferencia: sale de cuenta_id (efectivo/debito) y
--                     reduce la deuda de cuenta_destino_id (tarjeta).
--                     NO es un gasto: el gasto ya se registro al comprar.
--   'transferencia'-> movimiento entre cuentas propias (ej: retiro de debito a
--                     efectivo o consignacion de efectivo a debito).
--                     NO es ingreso ni gasto: no altera los totales del mes.
CREATE TABLE IF NOT EXISTS movimientos (
    id                 SERIAL PRIMARY KEY,
    usuario_id         INT           NOT NULL REFERENCES usuarios(id),
    fecha              DATE          NOT NULL,
    tipo               VARCHAR(15)   NOT NULL CHECK (tipo IN ('ingreso','gasto','pago_tarjeta','transferencia')),
    cuenta_id          INT           NOT NULL REFERENCES cuentas(id),
    cuenta_destino_id  INT           NULL REFERENCES cuentas(id),
    categoria_id       INT           NULL REFERENCES categorias(id),
    descripcion        VARCHAR(200)  NULL,
    monto              NUMERIC(14,2) NOT NULL CHECK (monto > 0),
    gasto_periodico_id INT           NULL REFERENCES gastos_periodicos(id),
    creado_en          TIMESTAMP     NOT NULL DEFAULT NOW()
);

-- Presupuesto mensual por categoria de gasto.
CREATE TABLE IF NOT EXISTS presupuestos (
    id            SERIAL PRIMARY KEY,
    usuario_id    INT           NOT NULL REFERENCES usuarios(id),
    categoria_id  INT           NOT NULL REFERENCES categorias(id),
    monto_mensual NUMERIC(14,2) NOT NULL CHECK (monto_mensual > 0),
    UNIQUE (usuario_id, categoria_id)
);

-- Preferencias personales de visualizacion y calculo.
CREATE TABLE IF NOT EXISTS configuraciones_usuario (
    usuario_id             INT PRIMARY KEY REFERENCES usuarios(id),
    incluir_saldo_anterior BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS metas_ahorro (
    id             SERIAL PRIMARY KEY,
    usuario_id     INT           NOT NULL REFERENCES usuarios(id),
    nombre         VARCHAR(100)  NOT NULL,
    monto_objetivo NUMERIC(14,2) NOT NULL CHECK (monto_objetivo > 0),
    fecha_objetivo DATE          NULL,
    color          VARCHAR(7)    NOT NULL DEFAULT '#6f42c1',
    activo         BOOLEAN       NOT NULL DEFAULT TRUE,
    creado_en      TIMESTAMP     NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS aportes_meta (
    id             SERIAL PRIMARY KEY,
    usuario_id     INT           NOT NULL REFERENCES usuarios(id),
    meta_ahorro_id INT           NOT NULL REFERENCES metas_ahorro(id),
    fecha          DATE          NOT NULL,
    monto          NUMERIC(14,2) NOT NULL CHECK (monto > 0),
    notas          VARCHAR(200)  NULL
);

CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_fecha ON movimientos (usuario_id, fecha);
CREATE INDEX IF NOT EXISTS idx_movimientos_cuenta        ON movimientos (cuenta_id);
CREATE INDEX IF NOT EXISTS idx_cuentas_usuario           ON cuentas (usuario_id);
CREATE INDEX IF NOT EXISTS idx_categorias_usuario        ON categorias (usuario_id);
CREATE INDEX IF NOT EXISTS idx_metas_usuario             ON metas_ahorro (usuario_id);
CREATE INDEX IF NOT EXISTS idx_usuarios_activo_suscripcion ON usuarios (activo, estado_suscripcion, proximo_pago);
CREATE INDEX IF NOT EXISTS idx_usuarios_bloqueado_hasta ON usuarios (bloqueado_hasta) WHERE bloqueado_hasta IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_usuario_tokens_hash_vigente ON usuario_tokens (token_hash, tipo, expira_en) WHERE usado_en IS NULL;
CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_tipo_fecha ON movimientos (usuario_id, tipo, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_categoria_fecha ON movimientos (usuario_id, categoria_id, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_cuenta_fecha ON movimientos (usuario_id, cuenta_id, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_movimientos_usuario_cuenta_destino_fecha ON movimientos (usuario_id, cuenta_destino_id, fecha DESC) WHERE cuenta_destino_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_movimientos_gasto_periodico ON movimientos (gasto_periodico_id) WHERE gasto_periodico_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_cuentas_usuario_activo_tipo ON cuentas (usuario_id, activo, tipo, nombre);
CREATE INDEX IF NOT EXISTS idx_categorias_usuario_activo_tipo ON categorias (usuario_id, activo, tipo, nombre);
CREATE INDEX IF NOT EXISTS idx_gastos_periodicos_usuario_activo_fecha ON gastos_periodicos (usuario_id, activo, proxima_fecha);
CREATE INDEX IF NOT EXISTS idx_gastos_periodicos_usuario_tipo_fecha ON gastos_periodicos (usuario_id, tipo, proxima_fecha);
CREATE INDEX IF NOT EXISTS idx_presupuestos_usuario_categoria ON presupuestos (usuario_id, categoria_id);
CREATE INDEX IF NOT EXISTS idx_aportes_meta_usuario_meta_fecha ON aportes_meta (usuario_id, meta_ahorro_id, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_metas_usuario_activo_fecha ON metas_ahorro (usuario_id, activo, fecha_objetivo);

-- El usuario administrador inicial (admin / admin123) lo crea la aplicacion
-- automaticamente al arrancar si la tabla usuarios esta vacia.

-- Modulo de prestamos (ver modulo_prestamos.sql para el detalle de cada campo)
CREATE TABLE IF NOT EXISTS personas (id SERIAL PRIMARY KEY, usuario_id INT NOT NULL REFERENCES usuarios(id), nombre VARCHAR(100) NOT NULL, telefono VARCHAR(30) NULL, email VARCHAR(100) NULL, documento VARCHAR(30) NULL, notas VARCHAR(300) NULL, activo BOOLEAN NOT NULL DEFAULT TRUE);
CREATE TABLE IF NOT EXISTS prestamos (id SERIAL PRIMARY KEY, usuario_id INT NOT NULL REFERENCES usuarios(id), persona_id INT NOT NULL REFERENCES personas(id), fecha DATE NOT NULL, capital NUMERIC(14,2) NOT NULL CHECK (capital > 0), tasa_mensual NUMERIC(6,3) NOT NULL CHECK (tasa_mensual >= 0), dia_pago_interes INT NULL CHECK (dia_pago_interes BETWEEN 1 AND 31), fecha_pago_capital DATE NULL, notas VARCHAR(300) NULL, estado VARCHAR(10) NOT NULL DEFAULT 'activo' CHECK (estado IN ('activo','pagado')));
CREATE TABLE IF NOT EXISTS prestamo_pagos (id SERIAL PRIMARY KEY, usuario_id INT NOT NULL REFERENCES usuarios(id), prestamo_id INT NOT NULL REFERENCES prestamos(id), fecha DATE NOT NULL, tipo VARCHAR(15) NOT NULL CHECK (tipo IN ('abono_capital','pago_interes')), monto NUMERIC(14,2) NOT NULL CHECK (monto > 0), notas VARCHAR(200) NULL, creado_en TIMESTAMP NOT NULL DEFAULT NOW());
CREATE INDEX IF NOT EXISTS idx_personas_usuario ON personas (usuario_id);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario ON prestamos (usuario_id);
CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_prestamo ON prestamo_pagos (prestamo_id);
CREATE INDEX IF NOT EXISTS idx_personas_usuario_activo_nombre ON personas (usuario_id, activo, nombre);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_estado_fecha ON prestamos (usuario_id, estado, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_persona_estado ON prestamos (usuario_id, persona_id, estado);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_fecha_pago_capital ON prestamos (usuario_id, fecha_pago_capital) WHERE fecha_pago_capital IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_usuario_prestamo_fecha ON prestamo_pagos (usuario_id, prestamo_id, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_usuario_tipo_fecha ON prestamo_pagos (usuario_id, tipo, fecha DESC);

-- Modulo de inversiones.
CREATE TABLE IF NOT EXISTS tipos_inversion (
    id         SERIAL PRIMARY KEY,
    usuario_id INT          NOT NULL REFERENCES usuarios(id),
    nombre     VARCHAR(80)  NOT NULL,
    color      VARCHAR(7)   NOT NULL DEFAULT '#D4AF37',
    icono      VARCHAR(50)  NOT NULL DEFAULT 'bi-graph-up-arrow',
    activo     BOOLEAN      NOT NULL DEFAULT TRUE,
    UNIQUE (usuario_id, nombre)
);

CREATE TABLE IF NOT EXISTS inversiones (
    id                   SERIAL PRIMARY KEY,
    usuario_id           INT           NOT NULL REFERENCES usuarios(id),
    nombre               VARCHAR(100)  NOT NULL,
    entidad              VARCHAR(100)  NULL,
    tipo                 VARCHAR(80)   NOT NULL,
    tipo_inversion_id    INT           NULL REFERENCES tipos_inversion(id),
    fecha_inicio         DATE          NOT NULL,
    capital_inicial      NUMERIC(16,2) NOT NULL CHECK (capital_inicial > 0),
    tasa                 NUMERIC(9,4)  NOT NULL DEFAULT 0 CHECK (tasa >= 0),
    periodo_tasa         VARCHAR(10)   NOT NULL DEFAULT 'anual' CHECK (periodo_tasa IN ('mensual','anual')),
    tipo_rendimiento     VARCHAR(10)   NOT NULL DEFAULT 'fijo' CHECK (tipo_rendimiento IN ('fijo','variable')),
    fecha_retorno        DATE          NULL,
    permanencia_meses    INT           NOT NULL DEFAULT 0 CHECK (permanencia_meses BETWEEN 0 AND 600),
    penalidad_retiro     NUMERIC(7,3)  NULL CHECK (penalidad_retiro BETWEEN 0 AND 100),
    renovacion_automatica BOOLEAN      NOT NULL DEFAULT FALSE,
    moneda               VARCHAR(5)    NOT NULL DEFAULT 'COP',
    color                VARCHAR(7)    NOT NULL DEFAULT '#D4AF37',
    icono                VARCHAR(50)   NOT NULL DEFAULT 'bi-graph-up-arrow',
    notas                VARCHAR(500)  NULL,
    estado               VARCHAR(12)   NOT NULL DEFAULT 'activa' CHECK (estado IN ('activa','cerrada')),
    creado_en            TIMESTAMP     NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inversion_movimientos (
    id           SERIAL PRIMARY KEY,
    usuario_id   INT           NOT NULL REFERENCES usuarios(id),
    inversion_id INT           NOT NULL REFERENCES inversiones(id) ON DELETE CASCADE,
    fecha        DATE          NOT NULL,
    tipo         VARCHAR(15)   NOT NULL CHECK (tipo IN ('aporte','retiro','rendimiento','costo')),
    monto        NUMERIC(16,2) NOT NULL CHECK (monto > 0),
    notas        VARCHAR(250)  NULL,
    creado_en    TIMESTAMP     NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS inversion_valoraciones (
    id           SERIAL PRIMARY KEY,
    usuario_id   INT           NOT NULL REFERENCES usuarios(id),
    inversion_id INT           NOT NULL REFERENCES inversiones(id) ON DELETE CASCADE,
    fecha        DATE          NOT NULL,
    valor        NUMERIC(16,2) NOT NULL CHECK (valor >= 0),
    notas        VARCHAR(250)  NULL,
    creado_en    TIMESTAMP     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_inversiones_usuario ON inversiones (usuario_id);
CREATE INDEX IF NOT EXISTS idx_tipos_inversion_usuario ON tipos_inversion (usuario_id);
CREATE INDEX IF NOT EXISTS idx_inversion_movimientos_inversion ON inversion_movimientos (inversion_id, fecha);
CREATE INDEX IF NOT EXISTS idx_inversion_valoraciones_inversion ON inversion_valoraciones (inversion_id, fecha);
CREATE INDEX IF NOT EXISTS idx_inversiones_usuario_estado_tipo ON inversiones (usuario_id, estado, tipo_inversion_id);
CREATE INDEX IF NOT EXISTS idx_inversiones_usuario_retorno ON inversiones (usuario_id, estado, fecha_retorno) WHERE fecha_retorno IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_inversion_movimientos_usuario_inversion_fecha ON inversion_movimientos (usuario_id, inversion_id, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_inversion_valoraciones_usuario_inversion_fecha ON inversion_valoraciones (usuario_id, inversion_id, fecha DESC);
