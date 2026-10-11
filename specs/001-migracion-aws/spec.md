# Especificación: Migración del Servicio de Auditoria a AWS Lambda

- **ID**: 001-migracion-aws
- **Estado**: aprobada <!-- borrador | aclarada | aprobada | implementada -->
- **Creada**: 2026-10-11
- **Entrada original**: "Ok now we must migrate the service to aws, follow the README in urban-alert/infrastructure. This service must be migrated to a lambda tell me if it has a database. Multimedia, geoespacial, reportes, usuarios and the network itself (check the urban-alert-plataforma repo in the aws migration folder) to understand how it's deployed. There is already a deployed amazon event bridge that replaces the rabbitmq". Decisiones del usuario en la conversación: base RDS propia; se elimina la autorización por reporte (sin acceso a la base de Reportes); sin Cognito («at the moment it should register only transactions in reportes»); se despliegan consumidor **y** API; la regla de EventBridge de Auditoria, la cola SQS y su conexión con la Lambda las crea este repo (la consola no permite crear la regla sin un destino ya existente). Fuentes en `fuentes/`: README de `UrbanAlert/infrastructure`, README y guía de integración de `urban-alert-plataforma`. Referencias de práctica: `Urban-Alert-multimedia` (spec 002, Lambda), `urban-alert-reportes` (spec 001, RDS propio y publicación en EventBridge).

## 1. Resumen

Llevar el Servicio de Auditoria (.NET 10), que hoy solo corre en contenedores locales y consume `ReporteCreadoEvent` de RabbitMQ, a AWS como **funciones sin servidor**: una que registra en la cadena de auditoría cada evento de creación de reporte que Reportes publica en el bus de EventBridge existente (a través de una cola propia), y otra que publica la API de consulta del historial y de la integridad por el API Gateway compartido. Los datos viven en una **base PostgreSQL gestionada propia** en la red privada de la plataforma. Por ahora no hay autenticación de clientes ni autorización por reporte. Toda la infraestructura queda como código y se aprovisiona con GitHub Actions en cada commit a `main`; otro flujo, manual, la elimina.

## 2. Problema y contexto

- En local, Auditoria comparte el contenedor PostgreSQL de Reportes (base `urbanalert_auditoria`, roles `audit_writer`/`audit_reader`, creados por `postgres-reportes/01-auditoria.sh`) y lee la tabla `Reportes` con el rol `reportes_reader` para autorizar por reporte (`fuentes/UrbanAlert-infrastructure-README.md`).
- Reportes ya está en AWS y publica cada creación en el bus `urban-alert-bus-eventos` (creado a mano, ningún repo lo gestiona) con `source = urban-alert.reportes`, `detail-type = ReporteCreado` y `detail = {idEvento, reporte{id, estado, idCoordenada, idUsuario, fecha}}` (enums como códigos `UPPER_SNAKE_CASE`). Sin una regla con destino, esos eventos se descartan: hoy **nada en AWS los audita**.
- El bus ya tiene una regla de Notificaciones (destino: cola `urban_alert_notificaciones`). Auditoria tendrá su propia regla con el mismo patrón, creada por este repo.
- La plataforma `Unal-Team-C/urban-alert-plataforma` (VPC privada, NAT, API Gateway, contrato SSM `/urban-alert/plataforma/`) está aprovisionada. Multimedia ya demuestra el patrón de funciones Lambda en esa red con rutas en el gateway.
- El código actual exige Cognito fuera de `Development`, autoriza cada consulta leyendo la base de Reportes, y solo protege la API con la cabecera `X-Urban-Gateway-Key` que añade el gateway local.
- La cadena de auditoría es **global** (un único «último hash»): registrar eventos en paralelo sin control provoca contención y agota conexiones de la base.

## 3. Usuarios y actores

- **Equipo de desarrollo**: publicar cambios del servicio y de su infraestructura con un commit a `main` y eliminar el entorno a mano.
- **Servicio de Reportes** (productor, externo): publica `ReporteCreado` en el bus; no cambia.
- **Clientes de la API** (frontend, revisores): consultar el historial de auditoría de un reporte y verificar su integridad por la URL del gateway.
- **Revisor / docente**: entender desde el repositorio qué infraestructura existe y cómo se despliega.

## 4. Historias de usuario

### HU-1: Registrar cada reporte creado en AWS (Prioridad: P1)

Como equipo queremos que cada reporte creado en Reportes quede registrado en la cadena de auditoría, para tener la misma trazabilidad que en local.

**Criterios de aceptación**
1. Dado el entorno aprovisionado, cuando se crea un reporte por el gateway, entonces en menos de 60 s aparece su evento `reporte.creado` en el historial de ese reporte, con el hash anterior y el hash calculado.
2. Dado el mismo evento entregado dos veces, cuando se procesa, entonces queda un único registro (idempotencia por identificador del evento) y la cadena no cambia.
3. Dados varios reportes creados a la vez, cuando se registran, entonces la cadena queda íntegra (la verificación de integridad no detecta roturas).
4. Dado un evento que no se puede procesar tras varios intentos, entonces queda retenido en una cola de mensajes fallidos para revisarlo, sin bloquear los siguientes ni perderse.
5. Dado un evento con un formato inesperado (falta un campo obligatorio), entonces no se registra nada y termina en la cola de fallidos, con un log que lo indica.

### HU-2: Consultar el historial y la integridad por el gateway (Prioridad: P1)

Como cliente de la API quiero consultar el historial de auditoría de un reporte y verificar su integridad a través de la URL del gateway.

**Criterios de aceptación**
1. Dado un reporte auditado, cuando se llama `GET /api/v1/auditoria/reportes/{reportId}` por el gateway, entonces responde 200 con sus eventos en orden y paginación (`limit` 1–500, `cursor`) como hoy.
2. Dado un reporte auditado, cuando se llama `GET /api/v1/auditoria/reportes/{reportId}/integridad`, entonces responde 200 con el resultado de la verificación.
3. Dado un identificador sin eventos, entonces el historial responde 200 con una lista vacía (sin consultar la base de Reportes).
4. Dado un `limit` o `cursor` inválido, entonces responde 400 como hoy.
5. Dada una ruta no registrada bajo `/api/v1/auditoria/`, entonces el gateway responde 404.
6. La API no puede invocarse por ningún camino distinto del gateway.

### HU-3: Base PostgreSQL propia (Prioridad: P1)

Como equipo queremos que Auditoria tenga su propia instancia de PostgreSQL gestionada, separada de la de Reportes.

**Criterios de aceptación**
1. Dado el entorno aprovisionado, entonces Auditoria tiene una instancia propia, privada, cifrada, con conexiones cifradas obligatorias, que solo admite conexiones de las funciones de Auditoria.
2. Dado el entorno aprovisionado, entonces el esquema `001_audit_store.sql` está aplicado y existen los roles de escritura (solo inserta eventos y actualiza la cabeza de la cadena) y de lectura (solo consulta); el registro usa el de escritura y la API el de lectura.
3. Dado un intento de modificar o borrar un evento registrado, entonces la base lo rechaza (append-only, como hoy).
4. Dado un nuevo aprovisionamiento sobre una base ya inicializada, entonces la inicialización es idempotente y no altera los datos.

### HU-4: Aprovisionar y desplegar con un commit a `main` (Prioridad: P1)

**Criterios de aceptación**
1. Dado un commit a `main`, cuando termina el flujo, entonces las funciones tienen la versión de ese commit, la base está inicializada, la cola está conectada a la regla y a la función de registro, y las rutas responden por el gateway.
2. Dado un commit que hace fallar las pruebas, entonces no se modifica nada en AWS y el flujo queda en rojo.
3. Dado un commit sin cambios, entonces el aprovisionamiento informa de 0 cambios.
4. Dados dos commits seguidos, entonces sus flujos se ejecutan uno detrás de otro.

### HU-5: Desaprovisionar el entorno a mano (Prioridad: P1)

**Criterios de aceptación**
1. Dado el flujo lanzado a mano con la confirmación exacta `urban-alert-auditoria`, entonces se eliminan primero las rutas, después la regla de Auditoria (deja de entregar eventos), la conexión de la cola con la función y las funciones, y después la base (con copia final restaurable), la cola y el resto.
2. Sin la confirmación exacta, no elimina nada y lo indica.
3. Desaprovisionar no toca la plataforma, el bus, la regla ni la cola de Notificaciones, ni recursos de otros servicios.
4. Dado el entorno desaprovisionado, un commit a `main` lo recrea completo.

### HU-6: Entender y reproducir el despliegue (Prioridad: P2)

Como revisor quiero que el repositorio documente la arquitectura, los recursos de arranque, los flujos, las rutas, la configuración en la nube, los costes y el inventario.

### Casos borde

- La plataforma no está aprovisionada → el aprovisionamiento falla antes de cambiar nada, indicándolo.
- El bus no existe → el aprovisionamiento falla antes de cambiar nada, indicándolo.
- Reportes publica mientras Auditoria no está aprovisionada (o está desaprovisionada) → esos eventos se pierden (EventBridge no los guarda); se documenta.
- La base no está disponible (arranque, mantenimiento) → los eventos se reintentan desde la cola; ninguno se pierde mientras no supere el máximo de intentos.
- El registro en la base termina pero la función falla antes de confirmar el mensaje → el reintento no duplica el evento (idempotencia).
- Llega un evento de otro `detail-type` a la cola (regla mal configurada) → no se registra y va a la cola de fallidos.
- Se desaprovisiona con el entorno ya eliminado → termina sin error indicando que no hay nada que eliminar.
- Se intenta borrar la base desde el aprovisionamiento → la protección contra borrado lo impide.
- El flujo se lanza desde otra rama o un fork → no obtiene credenciales de AWS.

## 5. Requisitos funcionales

- **RF-001**: En la nube, el servicio DEBE recibir los eventos de creación de reporte desde una **cola propia** que es destino de la regla de Auditoria en el bus `arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos`. La cola DEBE aceptar mensajes solo de esa regla.
- **RF-002**: Este repo DEBE crear y gestionar la regla de Auditoria `urban-alert-auditoria-reporte-creado` en ese bus, con el patrón `source = urban-alert.reportes`, `detail-type = ReporteCreado` y la cola de RF-001 como único destino. No DEBE crear, modificar ni leer otras reglas del bus.
- **RF-003**: El registro DEBE interpretar el `detail` de EventBridge (`idEvento`, `reporte{id, estado, idCoordenada, idUsuario, fecha}`, enums como códigos) y producir el mismo registro de auditoría que hoy produce el consumidor de RabbitMQ para `ReporteCreadoEvent` (mismo `eventType`, versión, payload normalizado y hash). `correlationId` sigue nulo.
- **RF-004**: El registro DEBE ser idempotente por identificador del evento y mantener la cadena global íntegra con entregas repetidas, desordenadas o simultáneas.
- **RF-005**: Un mensaje que falle DEBE reintentarse; tras un máximo de 5 intentos DEBE pasar a una cola de mensajes fallidos propia, retenida al menos 14 días. Un mensaje fallido no DEBE bloquear ni hacer reprocesar los demás del mismo lote.
- **RF-006**: La API DEBE publicarse en el gateway compartido solo con `GET /api/v1/auditoria/reportes/{reportId}` y `GET /api/v1/auditoria/reportes/{reportId}/integridad`, equivalentes a `/auditoria/reportes/...` en local, sin autenticación de clientes (`NONE`). No se publican salud, OpenAPI ni Scalar.
- **RF-007**: En la nube, la API NO DEBE exigir Cognito ni consultar la base de Reportes: cualquier llamada válida por el gateway obtiene el historial o la integridad de cualquier reporte (decisión del usuario; mismo nivel de apertura que Reportes). En local, el comportamiento actual (Cognito, modo de desarrollo, autorización por reporte, `X-Urban-Gateway-Key`) DEBE seguir disponible.
- **RF-008**: La API solo DEBE poder invocarse desde las rutas de Auditoria del gateway compartido.
- **RF-009**: Auditoria DEBE tener una instancia PostgreSQL gestionada propia en las subredes privadas de la plataforma, accesible solo desde sus funciones, con conexiones cifradas obligatorias, copias automáticas ≥ 7 días, protección contra borrado y copia final al desaprovisionar.
- **RF-010**: El esquema DEBE salir únicamente de `001_audit_store.sql` del repositorio, junto con los roles de escritura y de lectura, aplicado por una identidad administradora antes de que las funciones reciban eventos o tráfico, de forma idempotente. Las funciones de registro y de API usan los roles de escritura y de lectura respectivamente, nunca el administrador.
- **RF-011**: Las credenciales de la base DEBEN vivir en el almacén de secretos de AWS; no pueden aparecer en el repositorio, el paquete de las funciones, las variables de GitHub ni los logs.
- **RF-012**: Las funciones DEBEN escribir logs estructurados (una línea por entrada) con, al menos, el identificador del evento y del reporte en cada registro, y método, ruta, código y duración en cada petición de la API, sin datos de conexión.
- **RF-013**: Toda la infraestructura del servicio DEBE estar definida como código en este repositorio: base, secretos, reglas de red, roles propios, funciones, cola y cola de fallidos, su conexión con la función de registro, la regla de Auditoria con su destino, logs y rutas del gateway. Red y gateway se toman del contrato SSM de la plataforma en cada aprovisionamiento.
- **RF-014**: El servicio NO DEBE gestionar ni modificar la plataforma, el bus, la regla ni la cola de Notificaciones, ni recursos de otros microservicios ni de arranque.
- **RF-015**: El estado de la infraestructura DEBE guardarse en el bucket de estado compartido, cifrado y con bloqueo, bajo la clave `auditoria/`.
- **RF-016**: El flujo de aprovisionamiento DEBE ejecutarse en cada push a `main` y, en este orden: pruebas y comprobaciones estáticas; construcción del paquete; comprobar que la plataforma y el bus existen; recursos base (red, base, secretos, roles, cola); funciones; inicialización de la base (RF-010); conexión de la cola con la función; regla con su destino (solo cuando la función ya puede registrar, para no acumular eventos sin consumidor); rutas en el gateway; prueba de humo por el gateway. Un paso fallido detiene los siguientes.
- **RF-017**: Debe existir un flujo de desaprovisionamiento solo manual, con confirmación `urban-alert-auditoria`, en el orden de HU-5. La protección contra borrado de la base solo la levanta ese flujo.
- **RF-018**: Los flujos DEBEN autenticarse con el rol OIDC compartido (credenciales temporales, solo desde `main`) e impedir ejecuciones simultáneas sobre la misma infraestructura.
- **RF-019**: El repositorio DEBE documentar: arquitectura, recursos de arranque (política del rol), workflows, rutas publicadas, configuración en la nube, formato del evento esperado, cómo revisar y reprocesar la cola de fallidos, costes e inventario de recursos.
- **RF-020**: El entorno local (`dotnet run`, `deploy/docker/docker-compose.yml`, compose de `UrbanAlert/infrastructure` con RabbitMQ) y la suite de pruebas DEBEN seguir funcionando sin cambios en su forma de uso.

## 6. Requisitos no funcionales

- **RNF-001 (seguridad)**: Ningún secreto en el repositorio, el paquete, GitHub ni los logs; la base no es pública y solo admite las funciones de Auditoria; permisos de cada función mínimos (la de registro: leer de su cola y su secreto; la de API: su secreto); la cola solo acepta mensajes de la regla de Auditoria.
- **RNF-002 (conexiones)**: Con la concurrencia máxima, las conexiones simultáneas de las funciones a la base no superan 40 (la instancia más pequeña admite ~80).
- **RNF-003 (latencia del registro)**: El 95 % de los eventos queda registrado menos de 60 s después de publicarse.
- **RNF-004 (latencia de la API)**: Las consultas responden dentro del máximo de 30 s del gateway; en caliente, en menos de 1 s para páginas de hasta 100 eventos.
- **RNF-005 (idempotencia del aprovisionamiento)**: Dos aprovisionamientos seguidos sobre el mismo commit dan 0 cambios en el segundo.
- **RNF-006 (coste)**: Tamaños mínimos (una zona para la base, clase más pequeña adecuada), con el coste mensual documentado.
- **RNF-007 (inmutabilidad)**: Los eventos registrados no pueden modificarse ni borrarse por ningún rol de la aplicación.

## 7. Entidades clave

Sin entidades nuevas: el evento de auditoría (`audit_events`) y la cabeza de la cadena (`audit_chain_state`) no cambian. Recursos relevantes:

- **Evento de EventBridge `ReporteCreado`** (externo): lo produce Reportes; Auditoria solo lo recibe.
- **Regla de Auditoria**: selecciona en el bus los eventos `ReporteCreado` y los entrega a la cola.
- **Cola de Auditoria y cola de fallidos**: buffer entre la regla y la función de registro.
- **Entorno del servicio**: base propia, secretos, roles, funciones de registro, de API y de inicialización, rutas en el gateway, reglas de red y logs.

## 8. Fuera de alcance

- Crear o modificar el bus o cualquier recurso de Notificaciones.
- Cambios en Reportes, incluidos nuevos eventos (cambio de estado, verificación, asignación, rechazo, eliminación): spec propia en `urban-alert-reportes`. Auditoria solo audita la creación, como hoy.
- Autenticación de clientes (Cognito) y autorización por reporte en la nube; acceso a la base de Reportes.
- Archivado en S3 con Object Lock (hoy opcional y desactivado).
- Recuperar eventos publicados antes de que exista el destino de la regla.
- Reprocesado automático de la cola de fallidos (se documenta el manual).
- Validación de los flujos en pull requests; varios entornos; dominio propio.

## 9. Supuestos

- Reportes ya está desplegado y publica `ReporteCreado` con el formato de RF-003 (README de `urban-alert-reportes`, «Evento en EventBridge»).
- El usuario adjuntó al rol compartido de GitHub una política `github-actions-urban-alert-auditoria` con SQS sobre `urban-alert-auditoria-*`, mapeos de origen de eventos de Lambda y gestión de reglas `urban-alert-auditoria-*` del bus (crear, borrar, describir, destinos, etiquetas); las demás políticas compartidas (Lambda `urban-alert-*`, `PassRole`, logs de Lambda, RDS, secretos, grupos de seguridad, API Gateway, SSM) ya existen y bastan para el resto, como en Multimedia y Reportes.
- Los nombres de los recursos usan el prefijo `urban-alert-auditoria` (las políticas existentes se limitan por `urban-alert-*`).
- La API sin autenticación es aceptable mientras ningún servicio de UrbanAlert autentique clientes en AWS (decisión del usuario).
- Las funciones van en la red privada de la plataforma (para llegar a la base) y salen a internet por su NAT (secretos).

## 10. Métricas de éxito

- **MS-001**: Tras crear un reporte por el gateway, su evento aparece en `GET /api/v1/auditoria/reportes/{id}` en menos de 60 s.
- **MS-002**: Tras crear 20 reportes seguidos, la verificación de integridad de cada uno es correcta y la cola de fallidos está vacía.
- **MS-003**: El primer Provision en `main` termina en verde y un segundo sobre el mismo commit da 0 cambios.
- **MS-004**: Deprovision seguido de Provision recrea el entorno; Reportes, Multimedia y Notificaciones no se ven afectados.

## 11. Clarificaciones

### Sesión 2026-10-11 (conversación previa a la spec)
- P: ¿Base de datos? → R: **RDS PostgreSQL propia** (no compartir la de Reportes).
- P: ¿Autorización por reporte (lectura de la tabla `Reportes`)? → R: **se elimina**.
- P: ¿Cognito? → R: no; por ahora solo debe registrar las transacciones de Reportes.
- P: ¿Alcance? → R: **consumidor y API** (API abierta, como Reportes).
- P: ¿Regla de EventBridge? → R: la crea **este repo** junto con la cola y su conexión con la función (la consola exige un destino existente para crear la regla).
- P: ¿Evento de reporte validado/verificado? → R: hoy no existe; queda para una spec de Reportes.

---

### Checklist de calidad (autorrevisión antes de entregar)

- [x] Sin detalles de implementación (lenguajes, frameworks, APIs, estructura de código) — salvo nombres de recursos y del evento, que son contrato con otros repos
- [x] Cada requisito es testeable y no ambiguo
- [x] Criterios de aceptación con resultado observable
- [x] Casos borde identificados
- [x] Alcance acotado (fuera de alcance explícito)
- [x] Como máximo 3 `[NEEDS CLARIFICATION]` pendientes (0)
- [x] Coherente con `.sdd/constitution.md` (plantilla vacía; se verificará en el plan contra P1–P10 de la plataforma, como Multimedia)
