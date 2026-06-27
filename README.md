# Finanzas Personales

Mini sistema web de uso interno para controlar ingresos, gastos y tarjetas de credito.
Multi-usuario: cada usuario ingresa con su clave y solo ve su propia informacion.

## Stack

- ASP.NET Core 8 MVC (.NET 8)
- PostgreSQL 14+ con Dapper (sin ORM pesado)
- BCrypt para claves
- Bootstrap 5 + Chart.js (graficas) — responsive para celular y PC
- PWA: instalable en el celular como app (icono propio, pantalla completa)
- ClosedXML para exportar movimientos a Excel

## Estructura

```
FinanzasPersonales/
  FinanzasPersonales.sln
  sql/schema.sql                  <- esquema de la base de datos
  src/FinanzasPersonales.Web/     <- aplicacion web
```

## Puesta en marcha

1. Crear la base de datos y aplicar el esquema:
   ```
   psql -U postgres -c "CREATE DATABASE finanzas_personales;"
   psql -U postgres -d finanzas_personales -f sql/schema.sql
   ```
2. Ajustar la cadena de conexion en `src/FinanzasPersonales.Web/appsettings.json`.
3. Ejecutar:
   ```
   dotnet run --project src/FinanzasPersonales.Web --urls http://localhost:5180
   ```
   o abrir `FinanzasPersonales.sln` en Visual Studio y presionar F5.
4. Al primer arranque la aplicacion crea el usuario inicial:
   - Usuario: `admin`
   - Clave: `admin123` (cambiarla de inmediato en el menu superior derecho > Cambiar clave)

Cada usuario administra sus propias cuentas y categorias. Los usuarios nuevos
empiezan sin categorias y pueden crear las suyas o cargar una lista sugerida
desde la pantalla **Mis categorias**.

## Como funciona el control de tarjetas de credito

Es el punto critico del sistema y se maneja con dos tipos de movimiento distintos:

1. **Compra con tarjeta** = movimiento tipo *Gasto* con la tarjeta como medio de pago.
   - Cuenta como gasto del MES EN QUE SE HIZO la compra (gasto real).
   - Aumenta la deuda de la tarjeta.
2. **Pago de la tarjeta** = movimiento tipo *Pago tarjeta* (cuenta origen -> tarjeta).
   - NO es un gasto (el gasto ya se registro al comprar): es solo flujo de caja.
   - Disminuye el saldo de la cuenta origen y la deuda de la tarjeta.

Asi nunca se duplica el gasto y siempre se sabe: cuanto gastaste realmente cada mes
(tablero > Gastos del mes) y cuanto debes hoy en tarjetas (tablero > Deuda tarjetas).

## Funcionalidades

- **Tablero**: ingresos/gastos/balance del mes, deuda de tarjetas, torta de gastos por
  categoria, barras de ingresos vs gastos de los ultimos 12 meses, avance de
  presupuestos, saldos por cuenta y recordatorio de gastos periodicos pendientes.
- **Movimientos**: registro rapido (ingreso / gasto / pago de tarjeta), filtros por
  mes, tipo, cuenta y categoria, edicion, eliminacion y exportacion a Excel.
- **Gastos periodicos**: gastos que se repiten cada N meses (seguros, impuestos...).
  El sistema recuerda los pendientes y al registrarlos reprograma la proxima fecha.
- **Presupuestos**: tope mensual por categoria con barra de avance y alerta visual
  (verde < 80 %, amarillo >= 80 %, rojo >= 100 %).
- **Analisis financiero**: comparacion contra el mes anterior, tasa de ahorro,
  flujo acumulado diario, gastos fijos vs variables y variaciones por categoria.
- **Centro de alertas**: avisa sobre presupuestos cerca del limite, gastos
  periodicos pendientes y metas proximas.
- **Metas de ahorro**: objetivos personales con fecha, aportes, avance y aporte
  mensual sugerido.
- **Cuentas**: efectivo, debito y tarjetas de credito con saldo/deuda calculados.
- **Categorias**: de ingreso o gasto, con clase (fijo / variable / periodico) y color.
- **Usuarios** (solo admin): creacion y gestion; cada usuario solo ve lo suyo.
- **Experiencia movil**: barra inferior de navegacion, accion central para
  registrar movimientos y componentes adaptados para pantallas pequenas.
- **Identidad visual premium**: temas claro y oscuro con tokens reutilizables,
  navegacion lateral, encabezado superior y paleta purpura/dorada. La preferencia
  de tema se conserva por usuario en el navegador.
- **Asistente financiero**: recomendaciones explicables basadas en los movimientos,
  informe mensual imprimible/PDF, registro rapido mediante voz o lenguaje natural y
  recordatorios de cobro preparados para WhatsApp o correo.
- **Inversiones**: portafolio con rentabilidad fija o variable, tasas mensuales o
  anuales, permanencia minima, vencimientos, aportes, retiros, rendimientos,
  costos, valoraciones de mercado, proyecciones y tablero de diversificacion.

## Configuracion de notificaciones

Un administrador puede consultar el estado en **Administracion > Integraciones**.
Las credenciales SMTP y WhatsApp Business se cargan mediante `dotnet user-secrets`
en desarrollo o variables de entorno en produccion. No deben guardarse en Git.

## Tokens visuales principales

- Purpura principal: `#7C3AED`
- Purpura secundario: `#A78BFA`
- Dorado principal: `#D4AF37`
- Dorado claro: `#F2C94C`
- Fondo claro: `#F5F5F7`
- Fondo oscuro: `#1E1448`
- Sidebar oscuro: `#2D1B69`

## Instalar como app en el celular

1. Desplegar la aplicacion accesible desde el celular (misma red WiFi o servidor en nube).
2. Abrir la URL en Chrome (Android) o Safari (iPhone).
3. Menu del navegador > "Agregar a pantalla de inicio" / "Instalar app".

## Despliegue en servidor pequeno (nube)

Identico al patron del helpdesk: publicar con
`dotnet publish -c Release -o publish` y servir detras de IIS o Nginx.
Requisitos minimos: 1 vCPU, 1 GB RAM, PostgreSQL en la misma maquina.
Para exponerla a internet usar siempre HTTPS (certificado Let's Encrypt) porque
viaja informacion financiera y claves.
