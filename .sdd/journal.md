# Journal SDD

<!-- Bitácora append-only: añade entradas AL FINAL, no reescribas las anteriores.
     Formato:
     ## AAAA-MM-DD · <feature o "general"> · <evento>
     - detalle (qué, por qué, archivos, verificación)
     Las horas exactas de cada edición/checkpoint quedan en .sdd/activity.log. -->

## 2026-09-24 · general · Arnés SDD instalado
- Hooks (SessionStart, PostToolUse, PreCompact, Stop), 11 comandos /sdd-*, protocolo, plantillas e instalador.
- Probado: rastreo de ediciones, bloqueo en Stop, checkpoint vía STATE.md, reinyección de contexto, instalación/fusión en otro proyecto.

## 2026-10-11 · 001-migracion-aws · Spec creada
- Creada spec 001-migracion-aws: Auditoria a Lambda (registro desde SQS conectada a la regla manual de EventBridge, API abierta por el gateway), RDS propio, sin Cognito ni autorización por reporte. Fuentes copiadas a `fuentes/`; indicaciones técnicas en `notes.md`.
- Pendiente: nombre de la regla y permisos `events:PutTargets`/`RemoveTargets` (RF-002).

## 2026-10-11 · 001-migracion-aws · La regla la crea este repo
- La consola no deja crear la regla sin un destino existente: el usuario decide que este repo cree la regla `urban-alert-auditoria-reporte-creado` con la cola como destino (RF-002 resuelto). La política del rol necesita gestión de reglas `urban-alert-auditoria-*`.

## 2026-10-11 · 001-migracion-aws · Spec aprobada
- El usuario aprueba la spec («adelante») y confirma la política `github-actions-urban-alert-auditoria` adjunta al rol. Fase plan.

## 2026-10-11 · 001-migracion-aws · Plan redactado
- plan.md: 3 Lambdas `dotnet10` (api ASP.NET Core hosting, registro SQS, init), 2 .zip deterministas; 4 stacks base/servicio/eventos/rutas en `auditoria/`; RDS PG17 t4g.micro; SQS+DLQ, mapeo concurrencia 2; regla en stack `eventos` aplicado tras el init.
- Decisiones clave: D-003 contrato del evento copiado (la referencia al proyecto hermano no existe en CI), D-006 `Autenticacion:Modo=Ninguna`, D-007 `Gateway:ExigirClave=false` + `PathBase=/api/v1` (AWS_PROXY no reescribe rutas). research.md con runtime y versiones.

## 2026-10-11 · 001-migracion-aws · Plan aprobado
- El usuario aprueba el plan («Adelante»), confirma el contrato de D-003 y la forma de llamar por el gateway (D-007).

## 2026-10-11 · 001-migracion-aws · tasks.md generado
- 34 tareas: preparación 4, pruebas 7, núcleo 14, integración 5, pulido 4. Cobertura completa de RF/RNF.

## 2026-10-11 · 001-migracion-aws · Tareas aprobadas
- El usuario aprueba tasks.md («Adelante»). Empieza la implementación por T001.

## 2026-10-11 · 001-migracion-aws · T001 hecha
- Contrato copiado (ReporteCreadoEvent, ReporteEventoDto, EstadoReporte; mismos namespaces), sin ProjectReference al repo hermano; global.json (SDK 10.0.401, como Reportes). Dockerfile/compose de deploy/docker con contexto = raíz del repo (antes dependían del hermano; misma forma de uso).
- Verificación: build de la solución en verde (0 errores) y UnitTests 24/24 en sdk:10.0 (Podman).

## 2026-10-11 · 001-migracion-aws · T002 hecha
- Proyecto src/Lambda (Amazon.Lambda.Core 3.3.0, SQSEvents 3.0.1, Serialization.SystemTextJson 3.0.1); AWSSDK.SecretsManager 4.0.100.14 y AWSSDK.S3 4.0.0 → 4.0.103.4 (Core alineado; quita el aviso NU1901 de Core 4.0.0); AspNetCoreServer.Hosting 2.2.1; TestUtilities 4.2.0. Core 3.4.0 y TestUtilities 4.3.0 descartadas por tener < 2 semanas.
- Verificación: build 0 errores; UnitTests 24/24.

## 2026-10-11 · 001-migracion-aws · T003 hecha
- infra/bootstrap: github-auditoria-policy.json (copia de la adjuntada) y README (recursos compartidos, política propia, AWS_ROLE_ARN es secreto de la organización y no hay variables de repo → región fija en los workflows).
- Desviación registrada en plan D-010: la API no reserva concurrencia (límite de cuenta de 10, como en Multimedia).
- Verificación: JSON válido (json.tool); sin secretos.

## 2026-10-11 · 001-migracion-aws · T004 hecha
- infra/INVENTARIO.md: recursos por stack (§4.2), de arranque y ajenos (plataforma, bus, Notificaciones, Reportes).
- Verificación: cada recurso de §4.2 del plan aparece en su stack.

## 2026-10-11 · 001-migracion-aws · T005 hecha
- EventBridgeReporteCreadoTests: sobre real, estado código/número, igualdad con el registro de MassTransit, source/detail-type ajenos, detalle nulo/vacío/Guid.Empty/estado desconocido, cuerpo no JSON.
- Verificación: el build de UnitTests falla solo por CS0103/CS0246 (EventBridgeReporteCreado, EventoInvalidoException no existen).

## 2026-10-11 · 001-migracion-aws · T006 hecha
- RegistroHandlerTests: lote mixto (inválido + store que lanza → exactamente esos messageId en BatchItemFailures, el resto se persiste y archiva), lote sin errores, log Information por evento con EventId/ReportId/MessageId, error con MessageId para mensajes inválidos.
- Verificación: falla solo por compilación (namespace Auditoria.Lambda sin tipos).

## 2026-10-11 · 001-migracion-aws · T007 hecha
- ModoNubeTests (Mvc.Testing 10.0.12): historial con y sin prefijo /api/v1 → 200 sin token ni CorePostgres; integridad 200; limit=0 → 400 sin llamar al store; configuración por defecto sin cabecera → 403; modo Cognito sin token → 401 (con ExigirClave=false); modo desconocido no arranca.
- Verificación (copia temporal sin los tests de T005/T006, que aún no compilan): 5 fallan como se espera (modos inexistentes); pasan 403 por defecto y 'no arranca'.

## 2026-10-11 · 001-migracion-aws · T008 hecha
- ApiLambdaHostTests: subclase de APIGatewayHttpApiV2ProxyFunction (constructor con IServiceProvider: en FirstRequest el logger es nulo y MarshallRequest falla) traduce un payload 2.0 de stage $default → Path = ruta pública completa, PathBase vacío; esa ruta se envía a la API en modo nube.
- Verificación (copia temporal): la traducción pasa; la prueba falla en la API por 'Cognito obligatorio' (modos inexistentes), como se espera.

## 2026-10-11 · 001-migracion-aws · T009 hecha
- SecretosConexionTests: con ARN expone ConnectionStrings:AuditWriter desde connection_string; sin ARN no crea el cliente y conserva la cadena local; secreto sin connection_string (JSON o texto) → InvalidOperationException que nombra la clave y no contiene el secreto.
- Verificación: falla solo por compilación (Infrastructure.Configuracion no existe).

## 2026-10-11 · 001-migracion-aws · T010 hecha
- AuditDatabaseInitializerTests (contenedor propio postgres:17-alpine, como la RDS): base vacía → roles sin privilegios elevados y esquema; segunda ejecución conserva datos y cambia contraseñas (la antigua deja de valer); lector sin INSERT (42501); UPDATE/DELETE de eventos rechazados.
- API prevista: AuditDatabaseInitializer.InicializarAsync(adminConnectionString, writerPassword, readerPassword, ct).
- Verificación: falla solo por compilación (CS0103).

## 2026-10-11 · 001-migracion-aws · T011 hecha
- RegistroIntegracionTests (colección audit-postgres): mismo mensaje dos veces → 1 evento; 20 eventos en 2 invocaciones paralelas → integridad verificada para cada reporte. Añadido Amazon.Lambda.TestUtilities 4.2.0 a IntegrationTests.
- Verificación: falla solo por compilación (Auditoria.Lambda sin tipos).

## 2026-10-11 · 001-migracion-aws · T012 hecha
- EventBridgeReporteCreado.Leer (source/detail-type exactos, detail objeto, deserialización con CodigosEnum.ConfigurarMensajeria, ids no vacíos, estado definido, fecha) y EventoInvalidoException.
- Verificación (copia temporal sin los tests de T006/T009): EventBridgeReporteCreadoTests + existentes 25/25.

## 2026-10-11 · 001-migracion-aws · T013 hecha
- Program.cs: Autenticacion:Modo (Cognito | Ninguna; ModoAutenticacion en Infrastructure), Cognito solo obligatorio en modo Cognito, ModoDesarrollo incompatible con Ninguna, AddAWSLambdaHosting(HttpApi), HttpLogging (método, ruta, código, duración, CombineLogs), UsePathBase + UseRouting, filtro X-Urban-Gateway-Key condicionado a Gateway:ExigirClave (por defecto true). Cognito en AgregarCognito sin cambios de lógica. AutenticacionAbiertaHandler y SinAutorizacionService; DI elige el servicio de autorización por modo. appsettings.json con las claves nuevas (valores locales) y HttpLoggingMiddleware en Information.
- Verificación (copia temporal sin tests de T006/T009): UnitTests 46/46 (ModoNube, ApiLambdaHost, existentes).

## 2026-10-11 · 001-migracion-aws · T014 hecha
- SecretosConexion: IConfigurationSource que lee ConnectionStrings:<X>SecretArn de las fuentes anteriores, obtiene connection_string una vez y la expone como ConnectionStrings:<X>; errores sin el contenido del secreto. Program.cs la añade justo tras CreateBuilder.
- Verificación (copia temporal sin RegistroHandlerTests): UnitTests 50/50.

## 2026-10-11 · 001-migracion-aws · T015 hecha
- AuditDatabaseInitializer.InicializarAsync: CREATE/ALTER ROLE audit_writer/audit_reader (LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE, contraseña escapada) y 001_audit_store.sql como recurso embebido, en una transacción.
- Ajuste de T010: el cambio de contraseña se comprueba por el hash de pg_authid (el pg_hba de la imagen confía en conexiones locales).
- Verificación local (Podman, Testcontainers por el socket): AuditDatabaseInitializerTests 4/4 con --filter; la suite completa da 'Connection reset by peer' intermitentes al arrancar varios contenedores (también en las pruebas existentes, no relacionado con el cambio). Se confirmará en el job test de CI.

## 2026-10-11 · 001-migracion-aws · T016 hecha
- src/Lambda: Arranque (entorno + SecretosConexion + consola según Logging:Console, AddInfrastructure, RabbitMQ desactivado, LambdaSerializer), RegistroHandler (lote en serie, persistir + archivar, log por evento, fallos parciales; inválidos con su motivo), InitHandler (admin de RDS desde Init:AdminSecretArn + host/base/TLS de la cadena del escritor; errores solo con tipo o SQLSTATE; {"ok":true}).
- Verificación: build 0 errores; UnitTests 54/54; IntegrationTests RegistroIntegracionTests 2/2 y existentes 5/5 (Podman, por clase).

## 2026-10-11 · 001-migracion-aws · T017 hecha
- Imagen de deploy/docker construida desde la raíz del repo y arrancada en Development con modo de desarrollo: /health 200, /scalar/v1 200, logs JSON de HttpLogging. /auditoria/reportes/{id} sin CorePostgres falla como antes (documentado en el README: la autorización necesita esa cadena); sin cambios de comportamiento local.
- Hallazgo: HttpLogging registraba 200 en peticiones con excepción no controlada. Fuera de Development se añade UseExceptionHandler dentro del log (500 sin detalles); prueba ErrorDelAlmacen_Responde500SinDetalles.
- ContratoReportesTests: URN de MassTransit 'urn:message:Application.Reportes.Eventos:ReporteCreadoEvent' sin cambios.
- Verificación: UnitTests 56/56.

## 2026-10-11 · 001-migracion-aws · T018 hecha
- scripts/build-lambda-zips.sh: publish linux-x64 framework-dependent (sin apphost, ContinuousIntegrationBuild) de src/API → build/api.zip (6,2 MB) y src/Lambda → build/funciones.zip (3,9 MB); fechas y permisos fijos, orden estable; falla si > 50 MB. /build/ en .gitignore.
- Verificación: dos ejecuciones desde cero en sdk:10.0 dan el mismo sha256 en ambos paquetes; shellcheck sin avisos; el paquete de la API incluye 001_audit_store.sql y runtimeconfig.

## 2026-10-11 · 001-migracion-aws · T019 hecha
- infra/base: versions (TF >= 1.10, aws ~> 6.0, random ~> 3.6), backend auditoria/base.tfstate con use_lockfile, proveedor con etiquetas (Service=auditoria, Stack=base), variables con validaciones (prefijo, backups ≥ 7, DLQ ≥ 14 días, visibilidad), tfvars, network.tf (SSM de la plataforma; SG -lambda con salida 443 y 5432 solo al SG -rds; SG -rds con entrada 5432 solo desde -lambda; subnet group en todas las subredes privadas). .terraform/ en .gitignore.
- Verificación: terraform 1.16.5 init -backend=false + validate → Success.

## 2026-10-11 · 001-migracion-aws · T020 hecha
- rds.tf: PG 17 db.t4g.micro, gp3 20→50, una zona, privada, cifrada, backups según variable, manage_master_user_password, deletion_protection = !allow_destroy, copia final con sufijo.
- secrets.tf: random_password writer/reader (keepers db_password_version), secretos urban-alert-auditoria-db-writer/-reader con username/password/host/port/dbname/connection_string (SSL Require, GSS off, pool 2), recovery 0.
- Verificación: validate → Success.

## 2026-10-11 · 001-migracion-aws · T021 hecha
- sqs.tf: cola -eventos (SSE-SQS, 4 días, visibilidad 180 s con precondición ≥ 6 × timeout del registro, redrive a la DLQ con maxReceiveCount 5) y -eventos-dlq (14 días, redrive allow solo desde la principal).
- iam.tf: roles -api/-registro/-init con políticas en línea -runtime (ENI, solo sus secretos —api: reader; registro: writer; init: los dos + maestro de RDS—, su grupo de logs; registro además SQS sobre su cola). logs.tf: grupos /aws/lambda/... 30 días. outputs.tf sin contraseñas.
- Verificación: validate → Success; fmt -check sin diferencias.

## 2026-10-11 · 001-migracion-aws · T022 hecha
- infra/base/tests/base.tftest.hcl (mock_provider): protección y copia final por defecto, allow_destroy, RDS privada/cifrada/PG17/7 días/maestro gestionado, subnet group en todas las subredes, SG de RDS solo desde -lambda, colas (redrive 5, DLQ 14 días, visibilidad, SSE, nombres), precondición de visibilidad, roles por función con secretos mínimos y SQS solo en registro, cadenas con TLS y pool 2, prefijo obligatorio.
- Lockfile con hashes linux_amd64, linux_arm64 y darwin_arm64.
- Verificación: terraform test → 7 passed, 0 failed.
