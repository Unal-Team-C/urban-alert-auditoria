# Tareas: Migración del Servicio de Auditoria a AWS Lambda

- **Plan**: ./plan.md
- **Estado**: aprobado <!-- borrador | aprobado -->

Leyenda: `[ ]` pendiente · `[~]` en progreso · `[x]` hecha · `[!]` bloqueada · `[-]` descartada · `[P]` paralelizable

Formato de cada tarea (una línea):
`- [ ] T001 [P] Descripción imperativa — archivos: ruta/a, ruta/b — cubre: RF-001 — verifica: <comando o criterio observable>`

Dependencias: cada fase depende de la anterior; dentro de una fase se ejecuta en orden salvo `[P]`.

**Herramientas de verificación local** (sin SDK .NET, `terraform`, `aws` ni `actionlint` instalados; Podman con `podman machine start`):
- `$DOTNET` = `podman run --rm -v "$PWD":/w -w /w -v ~/.cache/nuget-podman:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0 dotnet`
- `$TF` = `podman run --rm -v "$PWD":/w -w /w docker.io/hashicorp/terraform:1.16.5` (`-chdir=infra/<stack> init -backend=false`, `validate`, `test`; `fmt -check -recursive infra`)
- `actionlint`: `podman run --rm -v "$PWD":/repo -w /repo docker.io/rhysd/actionlint:1.7.7`
- `shellcheck`: `podman run --rm -v "$PWD":/mnt -w /mnt docker.io/koalaman/shellcheck:v0.10.0 scripts/*.sh scripts/tests/*.sh`
- Integración (`$IT`): Testcontainers necesita el socket de Docker/Podman dentro del contenedor del SDK; si no es posible en local, se verifica en el job `test` de CI y se anota en el journal.

## Fase 1: Preparación

- [x] T001 Copiar el contrato del evento (D-003: `ReporteCreadoEvent`, `ReporteEventoDto`, `EstadoReporte` con sus namespaces), quitar la referencia a `UrbanAlert.Reportes` y fijar el SDK (`global.json`, `rollForward: latestPatch`) — archivos: src/Application/Reportes/Eventos/ReporteCreadoEvent.cs, src/Domain/Reportes/EstadoReporte.cs, src/Application/Auditoria.Application.csproj, global.json — cubre: RF-003, RF-020, P8 — verifica: `$DOTNET build UrbanAlert.Auditoria.slnx` en verde sin el repo hermano; `$DOTNET test tests/UnitTests` en verde (pruebas existentes)
- [x] T002 Añadir el proyecto `src/Lambda` (biblioteca, `net10.0`, `GenerateRuntimeConfigurationFiles`) con `Amazon.Lambda.Core`, `Amazon.Lambda.SQSEvents`, `Amazon.Lambda.Serialization.SystemTextJson`, y `AWSSDK.SecretsManager` en Infrastructure, `Amazon.Lambda.AspNetCoreServer.Hosting` en API y `Amazon.Lambda.TestUtilities` en UnitTests (versiones exactas de research.md con ≥ 2 semanas) — archivos: src/Lambda/Auditoria.Lambda.csproj, src/Infrastructure/Auditoria.Infrastructure.csproj, src/API/Auditoria.API.csproj, tests/UnitTests/UnitTests.csproj, UrbanAlert.Auditoria.slnx — cubre: D-001, D-002, P8 — verifica: `$DOTNET build UrbanAlert.Auditoria.slnx` en verde
- [ ] T003 [P] Documentar el arranque: política `github-auditoria-policy.json` (la que el usuario ya adjuntó), políticas compartidas reutilizadas, OIDC, bucket de estado, bus manual, `AWS_ROLE_ARN` — archivos: infra/bootstrap/README.md, infra/bootstrap/github-auditoria-policy.json — cubre: RF-019 — verifica: `python3 -m json.tool` válido; cada recurso con su dueño, sin secretos
- [ ] T004 [P] Crear el inventario de recursos por stack, de arranque y externos (plataforma, bus, recursos de Notificaciones como ajenos) — archivos: infra/INVENTARIO.md — cubre: RF-013, RF-014, RF-019 — verifica: cada recurso de §4.2 del plan aparece en su stack

## Fase 2: Pruebas

- [ ] T005 [P] Pruebas del sobre de EventBridge → `ReporteCreadoEvent` (JSON real de §7.1; `estado` como código y como número; `source`/`detail-type` erróneos, `detail` ausente, `idEvento` o `reporte.id` vacíos → excepción) y de que el `AuditEvent` resultante es igual al del consumidor MassTransit — archivos: tests/UnitTests/Auditoria/EventBridgeReporteCreadoTests.cs — cubre: RF-003 — verifica: falla solo por compilación
- [ ] T006 [P] Pruebas de `RegistroHandler` con `IAuditStore` simulado: lote de 3 con uno inválido y otro cuyo store lanza → `BatchItemFailures` contiene exactamente esos dos `messageId`; los demás se persisten; log por evento con `EventId`/`ReportId`/`MessageId` — archivos: tests/UnitTests/Lambda/RegistroHandlerTests.cs — cubre: RF-004, RF-005, RF-012 — verifica: falla solo por compilación
- [ ] T007 [P] Pruebas de la API en modo nube (`WebApplicationFactory`): `Autenticacion:Modo=Ninguna` + `Gateway:ExigirClave=false` + `Gateway:PathBase=/api/v1` y fuera de Development → `GET /api/v1/auditoria/reportes/{id}` 200 sin token y sin `CorePostgres` configurado; `/auditoria/reportes/{id}` sigue funcionando; `limit=0` → 400; con la configuración por defecto, sin cabecera del gateway → 403 (comportamiento actual) — archivos: tests/UnitTests/Auditoria/ModoNubeTests.cs — cubre: RF-006, RF-007, RF-020 — verifica: falla porque los modos no existen
- [ ] T008 [P] Prueba del host de Lambda: `APIGatewayHttpApiV2ProxyRequest` simulado con `rawPath=/api/v1/auditoria/reportes/{id}` invocado por la función de `Amazon.Lambda.AspNetCoreServer` → 200 (riesgo R-7) — archivos: tests/UnitTests/Lambda/ApiLambdaHostTests.cs — cubre: RF-006 — verifica: falla hasta T013
- [ ] T009 [P] Pruebas de `SecretosConexion`: con `ConnectionStrings:AuditWriterSecretArn` y un `IAmazonSecretsManager` simulado la configuración expone `ConnectionStrings:AuditWriter` = `connection_string` del JSON; sin ARN no llama a Secrets Manager; secreto sin `connection_string` → excepción sin el contenido del secreto en el mensaje — archivos: tests/UnitTests/Auditoria/SecretosConexionTests.cs — cubre: RF-011 — verifica: falla solo por compilación
- [ ] T010 [P] Pruebas de integración de `AuditDatabaseInitializer` sobre PostgreSQL vacío (Testcontainers): crea `audit_writer`/`audit_reader` y el esquema; segunda ejecución sin errores ni cambios de datos y con contraseñas resincronizadas; `audit_reader` no puede insertar; `UPDATE` de un evento rechazado — archivos: tests/IntegrationTests/Auditoria/AuditDatabaseInitializerTests.cs — cubre: RF-010, RNF-007 — verifica: falla solo por compilación ($IT o CI)
- [ ] T011 [P] Prueba de integración del registro real: mismo mensaje SQS dos veces → un solo `audit_events`; 20 eventos en 2 tareas paralelas → `VerifyAsync` íntegro para cada reporte — archivos: tests/IntegrationTests/Auditoria/RegistroIntegracionTests.cs — cubre: RF-004, MS-002 — verifica: falla solo por compilación ($IT o CI)

## Fase 3: Núcleo

- [ ] T012 Implementar `EventBridgeReporteCreado` (validación de `source`/`detail-type`, deserialización con `CodigosEnum.ConfigurarMensajeria`, campos obligatorios) — archivos: src/Application/Auditoria/EventBridgeReporteCreado.cs — cubre: RF-003 — verifica: T005 en verde — depende: T005
- [ ] T013 Modos de nube de la API: `Autenticacion:Modo` (`Cognito` | `Ninguna`, con `AutenticacionAbiertaHandler` y `SinAutorizacionService`), `Gateway:ExigirClave`, `Gateway:PathBase`, `AddAWSLambdaHosting(LambdaEventSource.HttpApi)`, `HttpLogging` sin cabeceras ni cuerpos — archivos: src/API/Program.cs, src/API/Autenticacion/AutenticacionAbiertaHandler.cs, src/Infrastructure/Persistencia/SinAutorizacionService.cs, src/Infrastructure/DependencyInjection.cs, src/API/appsettings.json — cubre: RF-006, RF-007, RF-008, RF-012, RF-020 — verifica: T007 y T008 en verde; pruebas existentes en verde — depende: T007, T008
- [ ] T014 Implementar `SecretosConexion` (fuente de configuración que resuelve `ConnectionStrings:<X>SecretArn` una vez por proceso) y usarla en API y Lambda — archivos: src/Infrastructure/Configuracion/SecretosConexion.cs, src/API/Program.cs — cubre: RF-011 — verifica: T009 en verde — depende: T009
- [ ] T015 Implementar `AuditDatabaseInitializer` (roles idempotentes con contraseñas de los secretos + `001_audit_store.sql`, en una transacción, como administrador del secreto de RDS) — archivos: src/Infrastructure/Persistencia/AuditDatabaseInitializer.cs — cubre: RF-010 — verifica: T010 en verde ($IT o CI) — depende: T010
- [ ] T016 Handlers de Lambda: `Arranque` (configuración de entorno + secretos + logging JSON + `AddInfrastructure` sin RabbitMQ), `RegistroHandler` (lote en serie, `SQSBatchResponse`) e `InitHandler` (`{"ok":true}`; errores sin datos de conexión) — archivos: src/Lambda/Arranque.cs, src/Lambda/RegistroHandler.cs, src/Lambda/InitHandler.cs — cubre: RF-003, RF-004, RF-005, RF-010, RF-012 — verifica: T006 y T011 en verde ($IT o CI) — depende: T006, T011, T012, T014, T015
- [ ] T017 Comprobar el entorno local sin cambios: `deploy/docker` compose + `dotnet run` (Development) y suite completa; el consumidor MassTransit sigue enlazando `Application.Reportes.Eventos:ReporteCreadoEvent` — archivos: (ninguno; ajustes si algo falla) — cubre: RF-020 — verifica: suites unitaria e integración en verde ($IT o CI); anotar en el journal — depende: T016
- [ ] T018 Script de paquetes deterministas: publica `src/API` → `build/api.zip` y `src/Lambda` → `build/funciones.zip` (linux-x64, framework-dependent, fechas normalizadas, orden fijo, falla si > 50 MB) — archivos: scripts/build-lambda-zips.sh, .gitignore — cubre: D-012, RNF-005 — verifica: dos ejecuciones seguidas en `$DOTNET`-imagen dan el mismo `sha256sum`; shellcheck sin avisos — depende: T016
- [ ] T019 Stack base, parte 1: versiones, backend `auditoria/base.tfstate`, proveedor con etiquetas, variables con validaciones (prefijo `urban-alert-auditoria`, backups ≥ 7), `terraform.tfvars`; red: SSM, SG `-lambda` (443 a todo, 5432 al SG de RDS), SG `-rds` (5432 solo desde `-lambda`), subnet group — archivos: infra/base/versions.tf, backend.tf, providers.tf, variables.tf, terraform.tfvars, network.tf — cubre: RF-009, RF-013, RF-015, RNF-001 — verifica: `$TF -chdir=infra/base init -backend=false && validate` → Success
- [ ] T020 Base, parte 2: RDS PG 17 (`db.t4g.micro`, gp3 20→50, una zona, privada, cifrada, 7 días, `manage_master_user_password`, `deletion_protection = !allow_destroy`, copia final) y secretos `-db-writer`/`-db-reader` (`random_password`, JSON con `connection_string` de §7.3) — archivos: infra/base/rds.tf, infra/base/secrets.tf — cubre: RF-009, RF-011, RNF-002, RNF-006 — verifica: `validate` en verde — depende: T019
- [ ] T021 Base, parte 3: colas `urban-alert-auditoria-eventos` (SSE-SQS, visibilidad 180 s, 4 días, redrive 5) y `-eventos-dlq` (14 días); roles `-api`, `-registro`, `-init` con políticas en línea de D-009; grupos de logs (30 días); salidas — archivos: infra/base/sqs.tf, infra/base/iam.tf, infra/base/logs.tf, infra/base/outputs.tf — cubre: RF-001, RF-005, RNF-001 — verifica: `validate` en verde — depende: T020
- [ ] T022 Pruebas del stack base (RDS privada/cifrada/backups/protección, SG de RDS solo desde `-lambda`, DLQ y redrive, visibilidad ≥ 6 × 30 s, cada rol solo con su secreto, `-registro` con SQS solo sobre su cola, nombres con prefijo) y lock con 3 plataformas — archivos: infra/base/tests/base.tftest.hcl, infra/base/.terraform.lock.hcl — cubre: RF-005, RF-009, RF-014, RNF-001 — verifica: `$TF -chdir=infra/base test` en verde — depende: T021
- [ ] T023 Stack servicio: 3 funciones (`dotnet10`, x86_64, handlers, memoria, timeouts y concurrencia de §7.4, VPC, variables de §7.3, etiqueta `Commit`), mapeo de origen (lote 10, ventana 5 s, `maximum_concurrency = 2`, `ReportBatchItemFailures`), salidas; pruebas (sin contraseñas en el entorno, sin URL de función, concurrencias, conexiones ≤ 40) y lock — archivos: infra/servicio/*.tf, infra/servicio/terraform.tfvars, infra/servicio/tests/servicio.tftest.hcl, infra/servicio/.terraform.lock.hcl — cubre: RF-004, RF-008, RF-011, RF-013, RNF-002 — verifica: `init -backend=false && validate && test` en verde — depende: T022
- [ ] T024 Stack eventos: regla `urban-alert-auditoria-reporte-creado` en el bus (patrón exacto), destino → cola, `aws_sqs_queue_policy` (solo `events.amazonaws.com` con `aws:SourceArn` = la regla); pruebas y lock — archivos: infra/eventos/*.tf, infra/eventos/terraform.tfvars, infra/eventos/tests/eventos.tftest.hcl, infra/eventos/.terraform.lock.hcl — cubre: RF-001, RF-002, RF-014 — verifica: `init -backend=false && validate && test` en verde — depende: T022
- [ ] T025 Stack rutas: integración `AWS_PROXY` 2.0, 2 rutas `GET` `NONE`, `aws_lambda_permission` `<exec-arn>/*/GET/api/v1/auditoria/reportes/*`, salida `api_endpoint`; pruebas y lock — archivos: infra/rutas/*.tf, infra/rutas/tests/rutas.tftest.hcl, infra/rutas/.terraform.lock.hcl — cubre: RF-006, RF-008 — verifica: `init -backend=false && validate && test` en verde; `$TF fmt -check -recursive infra` sin diferencias — depende: T023

## Fase 4: Integración

- [ ] T026 [P] Scripts de despliegue: `check-platform.sh` (copia de Multimedia + `describe-event-bus` del bus), `run-init.sh` (falla con `FunctionError` o respuesta ≠ `{"ok":true}`, sin imprimir la respuesta) y sus pruebas con `aws` simulado — archivos: scripts/check-platform.sh, scripts/run-init.sh, scripts/tests/run.sh, scripts/tests/fixtures/* — cubre: RF-010, RF-016 — verifica: `bash scripts/tests/run.sh` en verde (incluido «bus inexistente → falla»); shellcheck sin avisos
- [ ] T027 [P] Humo por el gateway (§4.3 paso 8) y su prueba con `curl`/`aws` simulados — archivos: scripts/smoke-gateway-auditoria.sh, scripts/tests/run.sh, scripts/tests/fixtures/* — cubre: RF-006, RF-016 — verifica: `bash scripts/tests/run.sh` en verde (falla si `/health` llega al servicio o si el mapeo no está `Enabled`); shellcheck sin avisos
- [ ] T028 [P] `crear-reporte-y-auditar.sh <gateway> [-n N]`: crea N reportes en Reportes, espera ≤ 60 s cada evento en Auditoria y comprueba la integridad — archivos: scripts/crear-reporte-y-auditar.sh — cubre: MS-001, MS-002 — verifica: shellcheck sin avisos; sin argumentos muestra el uso y sale ≠ 0
- [ ] T029 `provision.yml` (push a `main`, `concurrency: urban-alert-auditoria-aws` sin cancelación; job `test`; job `paquetes` con artefacto; job `provision` en el orden de §4.3 con resumen) — archivos: .github/workflows/provision.yml — cubre: RF-016, RF-018, RNF-005 — verifica: actionlint sin errores — depende: T018, T022, T023, T024, T025, T026, T027
- [ ] T030 `deprovision.yml` (manual, confirmación `urban-alert-auditoria`, solo `main`, misma `concurrency`; rutas → eventos → servicio → base `allow_destroy` → destroy; tolera estado vacío) — archivos: .github/workflows/deprovision.yml — cubre: RF-017, RF-018 — verifica: actionlint sin errores — depende: T029

## Fase 5: Pulido

- [ ] T031 README (sección AWS: arquitectura, orden, workflows, rutas y cómo llamarlas, configuración de §7.3, formato del evento, DLQ y reprocesado con `start-message-move-task`, costes, restauración) y CLAUDE.md (comandos de validación y gotchas) — archivos: README.md, CLAUDE.md, docs/funcionamiento-dotnet.md — cubre: RF-019, RNF-006 — verifica: cada sección enlaza al plan/inventario; sin secretos
- [ ] T032 Comprobación completa antes del PR: todas las verificaciones locales de §2 del plan; publicar la rama y abrir el PR (con permiso del usuario) — archivos: (ninguno) — cubre: todos — verifica: suites, `terraform test` ×4, `fmt`, actionlint, shellcheck y `scripts/tests/run.sh` en verde
- [ ] T033 Tras el merge (decisión del usuario): seguir el primer Provision en `main`, corregir con commits pequeños, y ejecutar `crear-reporte-y-auditar.sh` (MS-001, MS-002); segundo Provision sin cambios (MS-003) — archivos: (según fallos) — cubre: MS-001, MS-002, MS-003 — verifica: Provision en verde ×2; el script termina en 0; DLQ vacía
- [ ] T034 Deprovision + Provision (a mano, con permiso del usuario) y comprobar que Reportes, Multimedia y Notificaciones siguen respondiendo — archivos: (ninguno) — cubre: MS-004, HU-5 — verifica: ambos workflows en verde; humo de los demás servicios en verde

## Correcciones de verificación

<!-- /sdd-verify añade aquí tareas si detecta fallos -->

## Cobertura

| Requisito | Tareas |
|---|---|
| RF-001 | T021, T024 |
| RF-002 | T024 |
| RF-003 | T001, T005, T012, T016 |
| RF-004 | T006, T011, T016, T023 |
| RF-005 | T006, T016, T021, T022 |
| RF-006 | T007, T008, T013, T025, T027 |
| RF-007 | T007, T013 |
| RF-008 | T013, T023, T025 |
| RF-009 | T019, T020, T022 |
| RF-010 | T010, T015, T016, T026 |
| RF-011 | T009, T014, T020, T023 |
| RF-012 | T006, T013, T016 |
| RF-013 | T004, T019, T023 |
| RF-014 | T004, T022, T024 |
| RF-015 | T019 |
| RF-016 | T026, T027, T029 |
| RF-017 | T030 |
| RF-018 | T029, T030 |
| RF-019 | T003, T004, T031 |
| RF-020 | T001, T007, T013, T017 |
| RNF-001 | T019, T021, T022 |
| RNF-002 | T020, T023 |
| RNF-003 | T023, T033 |
| RNF-004 | T027, T033 |
| RNF-005 | T018, T029, T033 |
| RNF-006 | T020, T031 |
| RNF-007 | T010 |
