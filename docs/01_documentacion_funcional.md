# Documentacion funcional - Finanzas Personales

Version: 1.0
Fecha: 2026-06-27

## 1. Objetivo del sistema

Finanzas Personales es una aplicacion web para administrar gastos, ingresos, prestamos, inversiones, metas, calendario financiero, recordatorios e informacion de suscriptores. El sistema esta pensado para uso multiusuario: cada usuario trabaja con su propia informacion, categorias, cuentas, personas, prestamos e inversiones.

La experiencia esta dividida por permisos. Un usuario solo debe ver la documentacion y las opciones de los modulos que tiene habilitados.

## 2. Acceso, seguridad y cuenta

### Inicio de sesion

El acceso se realiza con correo electronico y contrasena. El correo debe estar verificado antes de permitir el ingreso.

El flujo es:

1. El administrador crea el usuario.
2. El sistema envia un correo de verificacion.
3. El usuario abre el enlace recibido.
4. El sistema marca el correo como verificado.
5. El usuario puede iniciar sesion.

Si el correo no esta verificado, el sistema bloquea el ingreso y muestra el mensaje correspondiente.

### Recuperacion de contrasena

El usuario puede solicitar recuperacion desde la pantalla de acceso. Si el correo existe, esta activo y verificado, el sistema envia un enlace temporal para cambiar la contrasena. El enlace vence y solo puede usarse una vez.

### Cambio de contrasena

Desde el menu del usuario autenticado se puede cambiar la contrasena. Debe ingresar la contrasena actual y una nueva contrasena fuerte.

La contrasena debe tener:

- Minimo 12 caracteres.
- Mayusculas.
- Minusculas.
- Numeros.
- Simbolos.

## 3. Permisos y visibilidad por usuario

El administrador define que puede ver cada suscriptor. Los permisos principales son:

- Gastos.
- Prestamos.
- Inversiones.

Permisos adicionales:

- Analisis directivo.
- Asistente financiero.
- Calendario financiero.

### Regla funcional de visibilidad

Si un usuario solo tiene permiso de Gestion de Finanzas:

- Puede ver Inicio, Gestion de Finanzas, Movimientos, Recurrentes, Presupuestos, Metas, Cuentas y Categorias.
- No debe ver Gestion de Prestamos, Personas, Analisis de prestamos, Gestion de Inversiones ni Portafolio.
- No debe ver documentacion funcional de modulos sin permiso.

Si un usuario solo tiene Prestamos:

- Puede ver Gestion de Prestamos, Personas y Analisis de prestamos.
- No debe ver Gestion de Finanzas, Cuentas, Categorias de gastos, Gestion de Inversiones ni Analisis directivo.

Si un usuario solo tiene Inversiones:

- Puede ver Portafolio, Tipos de inversion y Analisis de inversiones.
- No debe ver Gestion de Finanzas ni Gestion de Prestamos.

Los permisos adicionales se muestran solo si estan habilitados.

## 4. Pagina de inicio

La pagina de inicio funciona como portal financiero. Muestra tarjetas de acceso rapido y resumen segun los permisos del usuario.

Puede mostrar:

- Resumen de gastos e ingresos del mes.
- Deuda actual en tarjetas.
- Movimientos recurrentes pendientes.
- Resumen de prestamos activos.
- Interes pendiente por cobrar.
- Resumen de inversiones activas.
- Retornos esperados.
- Accesos rapidos a modulos permitidos.

El objetivo es que el usuario no caiga directamente en un tablero especifico, sino en una vista clara para decidir que desea hacer.

## 5. Gestion de Finanzas

Este modulo agrupa el control de ingresos, gastos, cuentas, categorias, presupuestos, metas de ahorro, recurrentes, importacion y analisis financiero.

### 5.1 Analisis financiero

Muestra informacion consolidada de ingresos, gastos y comportamiento mensual.

Incluye:

- Totales del mes.
- Comparacion de ingresos vs gastos.
- Grafica de flujo.
- Gastos por categoria.
- Presupuestos cercanos al limite.
- Movimientos recurrentes pendientes.
- Alertas financieras.

El tablero respeta el usuario autenticado y solo calcula informacion propia del usuario.

### 5.2 Movimientos

Permite registrar operaciones financieras diarias.

Tipos principales:

- Ingreso: dinero que entra a una cuenta.
- Gasto: dinero que sale de una cuenta.
- Pago de tarjeta: reduce deuda de tarjeta, pero no se cuenta como gasto doble.
- Transferencia: movimiento entre cuentas propias, no altera ingresos ni gastos.

Campos habituales:

- Fecha.
- Tipo.
- Cuenta origen.
- Cuenta destino cuando aplica.
- Categoria.
- Descripcion.
- Monto.

La lista permite filtrar por fechas. Los totales y exportaciones deben respetar el rango consultado.

### 5.3 Recurrentes

Antes eran solo gastos periodicos. Ahora el sistema maneja movimientos recurrentes:

- Gastos recurrentes.
- Ingresos recurrentes.

Ejemplos de gastos recurrentes:

- Arriendo pagado.
- Seguros.
- Servicios publicos.
- Matricula.
- Impuestos.

Ejemplos de ingresos recurrentes:

- Alquiler recibido.
- Nomina.
- Pago mensual de cliente.
- Pension.
- Renta de inmueble.

Al crear un recurrente se define:

- Tipo: gasto o ingreso.
- Nombre.
- Categoria.
- Cuenta habitual.
- Monto estimado.
- Frecuencia en meses.
- Proxima fecha.
- Estado activo o inactivo.

Cuando llega la fecha, el usuario usa Registrar. El sistema crea el movimiento real como ingreso o gasto y reprograma automaticamente la proxima fecha.

Los recurrentes tambien aparecen en el calendario financiero y en las alertas.

### 5.4 Cuentas

Permite registrar los lugares donde se mueve el dinero.

Tipos habituales:

- Efectivo.
- Cuenta debito.
- Tarjeta de credito.

Cada cuenta puede tener:

- Nombre.
- Tipo.
- Icono.
- Estado activo/inactivo.
- Dia de pago si es tarjeta de credito.

Las cuentas son por usuario. Cada usuario administra sus propias cuentas.

### 5.5 Categorias

Permite clasificar movimientos. Las categorias son por usuario y no son compartidas entre usuarios.

Cada categoria puede tener:

- Nombre.
- Tipo: ingreso o gasto.
- Clase: fijo, variable o periodico.
- Color.
- Icono.
- Estado.

Los iconos se muestran en listados, graficas, informes y calendario cuando aplica.

### 5.6 Presupuestos

Permite definir un limite mensual por categoria de gasto.

El sistema calcula cuanto se ha gastado en cada categoria durante el mes y genera alertas cuando se aproxima o supera el presupuesto.

### 5.7 Metas de ahorro

Permite crear objetivos de ahorro.

Cada meta tiene:

- Nombre.
- Monto objetivo.
- Fecha objetivo.
- Color.
- Estado.

El usuario puede registrar aportes. El sistema calcula avance, restante y cumplimiento.

### 5.8 Importacion

Permite importar movimientos desde archivo cuando el usuario maneja extractos o listados externos. El flujo normal es:

1. Cargar archivo.
2. Revisar datos.
3. Validar categorias/cuentas.
4. Confirmar importacion.

## 6. Gestion de Prestamos

El modulo de Prestamos permite administrar dinero prestado a personas, intereses, pagos y vencimientos.

### 6.1 Personas

Cada persona representa un contacto al que se le han realizado prestamos.

Campos:

- Nombre.
- Telefono.
- Documento.
- Email.
- Notas.
- Estado.

La lista de personas muestra resumen de deuda actual. Al hacer clic se abre el detalle consolidado de esa persona.

### 6.2 Resumen completo por persona

El detalle por persona muestra:

- Capital pendiente.
- Interes pendiente.
- Total pagado.
- Proxima fecha de cobro.
- Historial consolidado.
- Prestamos activos y pagados.

Desde esta vista se puede ir al detalle de cada prestamo.

### 6.3 Prestamos

Permite registrar un prestamo con:

- Persona.
- Fecha.
- Capital.
- Tasa mensual.
- Dia de pago de interes.
- Fecha pactada de pago de capital.
- Estado.

El sistema calcula:

- Saldo de capital.
- Interes mensual actual.
- Interes pendiente.
- Pagos aplicados.

### 6.4 Pagos de prestamos

En el detalle de un prestamo se pueden registrar:

- Pago de interes.
- Abono a capital.
- Pago total o parcial.

El historial muestra cada pago registrado.

### 6.5 Analisis de prestamos

Muestra una vista de control con:

- Capital total prestado.
- Saldo por cobrar.
- Intereses pendientes.
- Intereses cobrados.
- Prestamos activos.
- Prestamos atrasados.
- Graficas por persona o por periodo.

Los filtros pueden incluir rango de fechas, persona y estado.

## 7. Gestion de Inversiones

El modulo de Inversiones permite administrar capital colocado en diferentes instrumentos.

### 7.1 Tipos de inversion

Los tipos son personalizables por usuario.

Ejemplos:

- CDT.
- Fondo de inversion.
- Acciones.
- ETF.
- Criptoactivo.
- Inmueble.
- Participacion en negocio.
- Otra inversion.

Cada tipo puede tener nombre, color, icono y estado.

### 7.2 Portafolio

Permite registrar inversiones con:

- Nombre.
- Entidad.
- Tipo de inversion.
- Fecha de inicio.
- Capital inicial.
- Tasa.
- Periodo de tasa: mensual o anual.
- Tipo de rendimiento: fijo o variable.
- Fecha esperada de retorno.
- Permanencia minima.
- Penalidad por retiro.
- Renovacion automatica.
- Moneda.
- Color e icono.
- Notas.
- Estado.

### 7.3 Detalle de inversion

Permite registrar:

- Aportes.
- Retiros.
- Rendimientos.
- Costos.
- Valoraciones periodicas.

El sistema calcula rendimiento, capital actual estimado y movimientos relacionados.

### 7.4 Analisis de inversiones

Muestra:

- Capital invertido.
- Rendimientos registrados.
- Valor actual.
- Distribucion por tipo.
- Retornos proximos.
- Evolucion del portafolio.

## 8. Calendario financiero

El calendario muestra eventos financieros en formato calendario mensual, no solo como lista.

Puede incluir:

- Pagos de tarjetas.
- Movimientos recurrentes.
- Cobros de prestamos.
- Metas de ahorro.
- Retornos de inversiones.

Cada evento incluye:

- Fecha.
- Icono.
- Titulo.
- Detalle.
- Monto.
- Accion directa.

Ejemplos:

- Registrar gasto recurrente.
- Registrar ingreso recurrente.
- Registrar cobro de prestamo.
- Registrar aporte a meta.
- Registrar rendimiento de inversion.

## 9. Asistente financiero

El asistente financiero ayuda a detectar pendientes, recordatorios y recomendaciones.

Puede mostrar:

- Recordatorios de pagos.
- Recordatorios de cobros.
- Proximos vencimientos.
- Enlaces de WhatsApp o correo.
- Informe mensual.
- Registro rapido mediante lenguaje natural o voz cuando este habilitado.

## 10. Analisis directivo

Es un permiso adicional para vistas consolidadas de alto nivel.

Esta pensado para el propietario o usuarios avanzados.

Puede mostrar:

- Resumen global.
- Comparativos.
- Estado de modulos.
- Indicadores ejecutivos.
- Alertas principales.

## 11. Administracion de usuarios

Solo administradores pueden acceder.

Permite:

- Crear usuarios.
- Editar usuarios.
- Definir permisos.
- Configurar valor de suscripcion.
- Ciclo de suscripcion.
- Fecha de inicio.
- Proximo pago.
- Dias de gracia.
- Estado de suscripcion.
- Registrar pagos.
- Suspender por mora.
- Reactivar clientes.
- Reenviar verificacion de correo.

Los estados permiten controlar clientes activos, morosos, en prueba o cancelados.

## 12. Integraciones

Solo administradores pueden acceder.

### Correo SMTP

Permite configurar:

- Servidor SMTP.
- Puerto.
- Usuario.
- Remitente.
- Contrasena SMTP cifrada.
- SSL/TLS.

Se usa para:

- Verificacion de correo.
- Recuperacion de contrasena.
- Informes.
- Recordatorios.

### WhatsApp Business Cloud API

Permite dejar lista la integracion con WhatsApp para:

- Enviar recordatorios a personas que deben pagar.
- Avisar al administrador de proximos cobros.
- Avisar proximos gastos recurrentes.
- Enviar alertas financieras.

Requiere configuracion posterior en Meta:

- Phone Number ID.
- Access Token.
- Plantilla aprobada.
- Idioma de plantilla.
- Telefono administrador.

## 13. Uso en celular

La aplicacion esta pensada con interfaz responsive.

En celular se prioriza:

- Barra inferior.
- Accesos rapidos.
- Botones grandes.
- Tablas con desplazamiento.
- Tarjetas resumidas.
- Formularios por bloques.

El usuario debe poder registrar movimientos, consultar calendario y revisar pendientes sin depender de una pantalla grande.
