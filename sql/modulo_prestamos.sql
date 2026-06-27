-- ============================================================
-- Modulo de Prestamos: personas, prestamos y pagos.
-- Ejecutar conectado a la base finanzas_personales.
-- (Tambien queda incluido en schema.sql para instalaciones nuevas.)
-- ============================================================

CREATE TABLE IF NOT EXISTS personas (
    id         SERIAL PRIMARY KEY,
    usuario_id INT          NOT NULL REFERENCES usuarios(id),
    nombre     VARCHAR(100) NOT NULL,
    telefono   VARCHAR(30)  NULL,
    email      VARCHAR(100) NULL,
    documento  VARCHAR(30)  NULL,
    notas      VARCHAR(300) NULL,
    activo     BOOLEAN      NOT NULL DEFAULT TRUE
);

-- estado: 'activo' (con saldo) | 'pagado' (capital en cero)
CREATE TABLE IF NOT EXISTS prestamos (
    id                 SERIAL PRIMARY KEY,
    usuario_id         INT           NOT NULL REFERENCES usuarios(id),
    persona_id         INT           NOT NULL REFERENCES personas(id),
    fecha              DATE          NOT NULL,
    capital            NUMERIC(14,2) NOT NULL CHECK (capital > 0),
    tasa_mensual       NUMERIC(6,3)  NOT NULL CHECK (tasa_mensual >= 0), -- % de interes mensual
    dia_pago_interes   INT           NULL CHECK (dia_pago_interes BETWEEN 1 AND 31),
    fecha_pago_capital DATE          NULL, -- fecha pactada para devolver el capital (si aplica)
    notas              VARCHAR(300)  NULL,
    estado             VARCHAR(10)   NOT NULL DEFAULT 'activo' CHECK (estado IN ('activo','pagado'))
);

-- Historial de pagos del prestamo.
-- tipo: 'abono_capital' (reduce el saldo; el interes futuro se recalcula
--        automaticamente sobre el nuevo saldo) | 'pago_interes'
CREATE TABLE IF NOT EXISTS prestamo_pagos (
    id          SERIAL PRIMARY KEY,
    usuario_id  INT           NOT NULL REFERENCES usuarios(id),
    prestamo_id INT           NOT NULL REFERENCES prestamos(id),
    fecha       DATE          NOT NULL,
    tipo        VARCHAR(15)   NOT NULL CHECK (tipo IN ('abono_capital','pago_interes')),
    monto       NUMERIC(14,2) NOT NULL CHECK (monto > 0),
    notas       VARCHAR(200)  NULL,
    creado_en   TIMESTAMP     NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_personas_usuario        ON personas (usuario_id);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario       ON prestamos (usuario_id);
CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_prestamo ON prestamo_pagos (prestamo_id);
CREATE INDEX IF NOT EXISTS idx_personas_usuario_activo_nombre ON personas (usuario_id, activo, nombre);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_estado_fecha ON prestamos (usuario_id, estado, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_persona_estado ON prestamos (usuario_id, persona_id, estado);
CREATE INDEX IF NOT EXISTS idx_prestamos_usuario_fecha_pago_capital ON prestamos (usuario_id, fecha_pago_capital) WHERE fecha_pago_capital IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_usuario_prestamo_fecha ON prestamo_pagos (usuario_id, prestamo_id, fecha DESC);
CREATE INDEX IF NOT EXISTS idx_prestamo_pagos_usuario_tipo_fecha ON prestamo_pagos (usuario_id, tipo, fecha DESC);
