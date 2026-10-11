# Plan técnico: Migración del Servicio de Auditoria a AWS Lambda

- **Spec**: ./spec.md
- **Estado**: aprobado <!-- borrador | aprobado -->
- **Fecha**: 2026-10-11

## 1. Resumen técnico

Tres funciones Lambda con el runtime gestionado `dotnet10` (x86_64) en las subredes privadas de la plataforma:

- **`urban-alert-auditoria-api`**: el host ASP.NET Core actual sobre `Amazon.Lambda.AspNetCoreServer.Hosting` (HTTP API, payload 2.0). Los controladores no cambian.
- **`urban-alert-auditoria-registro`**: un handler de SQS que interpreta el sobre de EventBridge y reutiliza `DotNetAuditEventFactory` y `AuditStore`.
- **`urban-alert-auditoria-init`**: aplica `001_audit_store.sql` y sincroniza los roles `audit_writer` y `audit_reader`.

Los eventos llegan así: regla `urban-alert-auditoria-reporte-creado` en el bus manual → cola SQS con DLQ → mapeo de origen de eventos con concurrencia máxima 2 y fallos parciales de lote.

La base es una RDS PostgreSQL 17 propia (`db.t4g.micro`), como la de Reportes. Las credenciales están en Secrets Manager, un secreto por rol.

Cuatro stacks de Terraform (`base`, `servicio`, `eventos`, `rutas`), con estado en `s3://urban-alert-tfstate-348364742283/auditoria/`. Se aplican solo desde GitHub Actions.

En local todo sigue igual: Cognito o modo de desarrollo, autorización por reporte, cabecera del gateway y RabbitMQ. Los cambios de la nube se activan solo por configuración.

## 2. Contexto técnico

| Aspecto | Valor |
|---|---|
| Lenguaje / versión | C# / .NET 10 (`net10.0`); runtime de Lambda `dotnet10` (gestionado desde 2026-01, ver research.md); Terraform `>= 1.10, < 2.0` (CI y local con `hashicorp/terraform:1.16.5`), proveedor `hashicorp/aws ~> 6.0`, `hashicorp/random ~> 3.6` |
| Dependencias nuevas | `Amazon.Lambda.AspNetCoreServer.Hosting` 2.2.1, `Amazon.Lambda.Core` 3.4.0, `Amazon.Lambda.SQSEvents` 3.0.1, `Amazon.Lambda.Serialization.SystemTextJson` 3.0.1, `AWSSDK.SecretsManager` 4.0.100.x (versiones exactas fijadas al implementar, con ≥ 2 semanas de antigüedad) |
| Dependencia retirada | Referencia de proyecto a `../../../UrbanAlert/backend/UrbanAlert.Reportes/src/Application` (no existe en CI): el contrato del evento se copia (D-003) |
| Almacenamiento | RDS PostgreSQL 17 `urban-alert-auditoria-db` (`db.t4g.micro`, 20→50 GB gp3, una zona, TLS forzado por `rds.force_ssl=1`), base `urbanalert_auditoria` |
| Mensajería | Bus `urban-alert-bus-eventos` (manual); regla y colas de este repo |
| Pruebas | xUnit: `tests/UnitTests` (Moq) y `tests/IntegrationTests` (Testcontainers PostgreSQL); `terraform test` con `mock_provider`; actionlint 1.7.7; shellcheck v0.10.0; humo por el gateway |
| Plataforma objetivo | AWS 348364742283, us-east-2; contrato SSM `/urban-alert/plataforma/*` |
| Restricciones | Rol OIDC compartido solo desde `refs/heads/main`; permisos limitados a `urban-alert-*`; integración del gateway ≤ 30 s; evento síncrono ≤ 6 MB; mínimo de `maximum_concurrency` en SQS = 2 |

Validación local (Podman, sin instalar nada), desde la raíz del repo:

```bash
SDK='podman run --rm -v "$PWD":/w -w /w -v ~/.cache/nuget-podman:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0'
eval "$SDK dotnet test tests/UnitTests/UnitTests.csproj"
TF='podman run --rm -v "$PWD":/w -w /w docker.io/hashicorp/terraform:1.16.5'
for s in base servicio eventos rutas; do eval "$TF -chdir=infra/$s init -backend=false" && eval "$TF -chdir=infra/$s validate" && eval "$TF -chdir=infra/$s test"; done
eval "$TF fmt -check -recursive infra"
podman run --rm -v "$PWD":/repo -w /repo docker.io/rhysd/actionlint:1.7.7
podman run --rm -v "$PWD":/mnt -w /mnt docker.io/koalaman/shellcheck:v0.10.0 scripts/*.sh scripts/tests/*.sh
```

## 3. Verificación contra la constitución

La constitución de este repo es la plantilla vacía (0.0.0). Igual que Multimedia y Reportes, el plan se verifica contra los principios P1–P10 de `urban-alert-plataforma`.

| Principio | ¿Cumple? | Nota |
|---|---|---|
| P1 IaC solo desde Actions | Sí | En local solo `init -backend=false`, `validate`, `test` y `fmt` |
| P2 Arranque fuera de Terraform | Sí | La política `github-actions-urban-alert-auditoria` (ya adjunta) se versiona en `infra/bootstrap/`. El bus es manual |
| P3 Sin secretos | Sí | OIDC. Las funciones solo reciben ARN de secretos. Las contraseñas solo están en el estado cifrado. No se imprimen nunca en los logs del workflow (lección de Reportes, run 38160461687) |
| P4 Destructivo solo explícito | Sí | RDS con `deletion_protection = !allow_destroy` y copia final. Solo Deprovision la levanta |
| P5 Coste mínimo documentado | Sí | `db.t4g.micro`, una zona, Lambda y SQS por uso (§4.6) |
| P6 Validación antes de apply | Sí | Job `test` antes de cualquier cambio |
| P7 Pruebas de lo que protege | Sí | Un `.tftest.hcl` por stack; pruebas unitarias y de integración (§9) |
| P8 Versiones fijadas | Sí | Terraform, proveedores, lockfiles con 3 plataformas, actions por SHA o versión, paquetes NuGet exactos y `global.json` |
| P9 Contrato estable | Sí | Solo consume `/urban-alert/plataforma/*` y el bus. Prefijo `urban-alert-auditoria`. Etiquetas por defecto |
| P10 Serializado e idempotente | Sí | Grupo de concurrencia `urban-alert-auditoria-aws` sin cancelación. El .zip determinista hace que repetir un commit no cambie nada |

## 4. Arquitectura y componentes

### 4.1 Vista general

```text
 Reportes ─PutEvents─► bus urban-alert-bus-eventos (manual)
                         ├─ regla de Notificaciones ─► cola urban_alert_notificaciones   (no se toca)
                         └─ regla urban-alert-auditoria-reporte-creado ─► SQS urban-alert-auditoria-eventos
                                                                              │  (5 intentos) └─► DLQ urban-alert-auditoria-eventos-dlq
                                                       mapeo de eventos (lote 10, concurrencia ≤ 2, fallos parciales)
 ┌── VPC de la plataforma, subredes privadas ─────────────────────▼──────────────────────────────────────┐
 │  Lambda registro ──TLS:5432 (audit_writer)──►                                                         │
 │  Lambda api      ──TLS:5432 (audit_reader)──►  RDS urban-alert-auditoria-db                           │
 │  Lambda init     ──TLS:5432 (postgres)──────►  (solo desde el SG de las funciones)                    │
 │        └──HTTPS─► NAT ─► Secrets Manager                                                               │
 └───────────────────────────────────────────────────────────────────────────────────────────────────────┘
 Cliente ─HTTPS─► API Gateway compartido ─ GET /api/v1/auditoria/reportes/{reportId}[/integridad] ─AWS_PROXY 2.0─► Lambda api
```

### 4.2 Stacks y recursos

| Stack | Estado | Recursos | ¿Lo destruye Deprovision? |
|---|---|---|---|
| `base` | `auditoria/base.tfstate` | SG `urban-alert-auditoria-lambda` (salida 443 a todo y 5432 al SG de RDS); SG `urban-alert-auditoria-rds` (entrada 5432 solo desde el SG `-lambda`); subnet group; RDS (`manage_master_user_password`, cifrada, 7 días de copias, copia final); `random_password` ×2; secretos `urban-alert-auditoria-db-writer` y `-db-reader` (JSON con `username`, `password`, `host`, `port`, `dbname`, `connection_string`); colas `urban-alert-auditoria-eventos` (SSE-SQS, visibilidad 180 s, retención 4 días, redrive `maxReceiveCount = 5`) y `-eventos-dlq` (14 días); roles `-api`, `-registro` e `-init` con políticas en línea mínimas; grupos de logs `/aws/lambda/urban-alert-auditoria-{api,registro,init}` (30 días) | Sí: primero `apply allow_destroy=true` y después `destroy` (copia `urban-alert-auditoria-final-<run_id>`) |
| `servicio` | `auditoria/servicio.tfstate` | Las 3 `aws_lambda_function` (D-001, D-002) y `aws_lambda_event_source_mapping` cola → registro (`batch_size = 10`, `maximum_batching_window_in_seconds = 5`, `scaling_config.maximum_concurrency = 2`, `function_response_types = ["ReportBatchItemFailures"]`) | Sí |
| `eventos` | `auditoria/eventos.tfstate` | `aws_cloudwatch_event_rule` en el bus (patrón de RF-002), `aws_cloudwatch_event_target` → cola, `aws_sqs_queue_policy` (solo `events.amazonaws.com` con `aws:SourceArn` = la regla) | Sí, el segundo |
| `rutas` | `auditoria/rutas.tfstate` | Integración `AWS_PROXY` 2.0 → función api; rutas `GET /api/v1/auditoria/reportes/{reportId}` y `GET /api/v1/auditoria/reportes/{reportId}/integridad` (`NONE`); `aws_lambda_permission` limitado a `<api-execution-arn>/*/GET/api/v1/auditoria/reportes/*` | Sí, el primero |

Dependencias: `servicio` lee `base`; `eventos` lee `base` (ARN y URL de la cola); `rutas` lee `servicio`. Todos usan `terraform_remote_state` sobre el mismo bucket. Desde SSM, `base` lee VPC y subredes, y `rutas` lee `api-id`, `api-execution-arn` y `api-endpoint`.

### 4.3 Orden de levantamiento (Provision, push a `main`)

| # | Paso | Por qué en este orden |
|---|---|---|
| 0 | **test**: `dotnet test` unitarias e integración (Docker del runner); `fmt`, `validate` y `test` de los 4 stacks; actionlint; shellcheck | No se toca AWS si algo falla (HU-4 #2) |
| 1 | **Paquetes** (`scripts/build-lambda-zips.sh` → `build/api.zip`, `build/funciones.zip`): `dotnet publish -c Release -r linux-x64 --self-contained false` y zip determinista; falla si alguno supera 50 MB comprimido | Antes de tener credenciales. Se suben como artefacto |
| 2 | **Comprobaciones**: `scripts/check-platform.sh` (contrato SSM) y `aws events describe-event-bus` del bus | RF-016 / casos borde: falla antes de cambiar nada |
| 3 | **`base`** | Red, base, secretos, colas y roles que necesitan las funciones. RDS tarda unos 10 min solo la primera vez |
| 4 | **`servicio`** | Funciones y mapeo. Las colas ya existen, pero están vacías: aún no hay regla |
| 5 | **Init** (`scripts/run-init.sh`): `aws lambda invoke` de `-init`; falla si hay `FunctionError` o la respuesta no es `{"ok":true}` | Esquema y roles antes de recibir eventos o tráfico (RF-010). También prueba red, secretos y TLS |
| 6 | **`eventos`** | La regla empieza a entregar solo cuando el registro ya puede escribir |
| 7 | **`rutas`** | La API se publica cuando la base está lista |
| 8 | **Humo** (`scripts/smoke-gateway-auditoria.sh`): `GET .../reportes/<uuid aleatorio>` → 200 y `eventos: []`; `.../integridad` → 200; `GET /api/v1/auditoria/health` → 404; `GET .../reportes/no-es-uuid` → 404; mapeo `Enabled`; regla `ENABLED`; DLQ vacía (aviso, no fallo) | Cadena completa sin escribir datos |
| 9 | **Resumen**: URL del gateway, SHA, tamaños de paquete, plan de cada stack, mensajes en la DLQ | Trazabilidad |

### 4.4 Orden de desmontaje (Deprovision, manual, confirmación `urban-alert-auditoria`)

1. **`rutas`** → destroy. El gateway responde 404 en `/api/v1/auditoria/*`.
2. **`eventos`** → destroy. La regla deja de entregar; el bus y la regla de Notificaciones no se tocan.
3. **`servicio`** → destroy, con .zip vacíos como variables. Las ENI de Lambda empiezan a liberarse.
4. **`base`** → `apply -var allow_destroy=true -var final_snapshot_suffix=<run_id>` y después `destroy`. Quedan la copia final y las colas borradas, con su contenido. El borrado del SG puede tardar 20–40 min por las ENI de Lambda (riesgo R-4).

Un stack vacío se informa y se salta.

### 4.5 Restaurar datos tras reaprovisionar (manual)

1. `aws rds restore-db-instance-from-db-snapshot` de `urban-alert-auditoria-final-<run_id>` a una instancia temporal.
2. `pg_dump`/`pg_restore` de `audit_events` y `audit_chain_state` hacia la nueva, desde una máquina en la VPC.

Va documentado en el README como procedimiento excepcional.

### 4.6 Costes (estimación mensual, us-east-2, 730 h)

| Recurso | Precio | Encendido | Desaprovisionado |
|---|---|---|---|
| RDS db.t4g.micro, una zona | ~0,016 USD/h | ~11,7 USD | 0 |
| Almacenamiento gp3 20 GB + copias | 0,115 USD/GB-mes | ~2,3 USD | copia final: céntimos |
| Secrets Manager (2 propios + el de RDS) | 0,40 USD/secreto | 1,2 USD | 0 |
| SQS (2 colas) | 0,40 USD/millón (primer millón gratis) | ~0 | 0 |
| Lambda (3 funciones) | por petición y GB-s (capa gratuita) | ~0 | 0 |
| EventBridge (entrega a la regla) | eventos personalizados ya pagados por Reportes; reglas sin coste | 0 | 0 |
| CloudWatch Logs (30 días) | 0,50 USD/GB | ~0 | 0 |
| **Total** | | **~15,2 USD** | **céntimos** |

No incluye la plataforma (~36,6 USD/mes). La salida a Secrets Manager pasa por el NAT de la plataforma, unos pocos KB por arranque en frío.

## 5. Estructura de archivos

```text
global.json                                   nuevo: SDK 10.0.x fijado (como Reportes)
UrbanAlert.Auditoria.slnx                     + src/Lambda, + carpeta infra/scripts
src/Domain/Reportes/EstadoReporte.cs          nuevo: copia del enum de Reportes (D-003), namespace Domain.Reportes
src/Application/Reportes/Eventos/ReporteCreadoEvent.cs  nuevo: copia del contrato, namespace Application.Reportes.Eventos (URN de MassTransit intacto)
src/Application/Auditoria.Application.csproj  − ProjectReference a UrbanAlert.Reportes
src/Application/Auditoria/EventBridgeReporteCreado.cs   nuevo: sobre de EventBridge → ReporteCreadoEvent (valida source/detail-type)
src/Infrastructure/Configuracion/SecretosConexion.cs    nuevo: ConnectionStrings:<X> desde Secrets Manager si existe ConnectionStrings:<X>SecretArn
src/Infrastructure/Persistencia/AuditDatabaseInitializer.cs nuevo: roles + 001_audit_store.sql, idempotente
src/Infrastructure/Persistencia/SinAutorizacionService.cs  nuevo: IAuditAuthorizationService sin restricciones (modo Ninguna)
src/Infrastructure/DependencyInjection.cs     + modo de autorización, + secretos
src/API/Program.cs                            + AddAWSLambdaHosting(HttpApi), + Autenticacion:Modo, + Gateway:PathBase, + Gateway:ExigirClave, + logs JSON/HttpLogging
src/API/Autenticacion/AutenticacionAbiertaHandler.cs   nuevo: principal fijo (modo Ninguna)
src/API/Auditoria.API.csproj                  + Amazon.Lambda.AspNetCoreServer.Hosting
src/Lambda/Auditoria.Lambda.csproj            nuevo: handlers de registro e init
src/Lambda/RegistroHandler.cs                 SQSEvent → SQSBatchResponse
src/Lambda/InitHandler.cs                     → {"ok":true}
src/Lambda/Arranque.cs                        ServiceProvider compartido (config de entorno + secretos + logging JSON)
tests/UnitTests/Auditoria/EventBridgeReporteCreadoTests.cs, RegistroHandlerTests.cs, ModoNingunaTests.cs, SecretosConexionTests.cs, PathBaseTests.cs
tests/IntegrationTests/Auditoria/AuditDatabaseInitializerTests.cs
infra/
  bootstrap/README.md, github-auditoria-policy.json
  base/     {backend,versions,providers,variables,network,rds,secrets,sqs,iam,logs,outputs}.tf terraform.tfvars .terraform.lock.hcl tests/base.tftest.hcl
  servicio/ {backend,versions,providers,variables,main,outputs}.tf terraform.tfvars .terraform.lock.hcl tests/servicio.tftest.hcl
  eventos/  {backend,versions,providers,variables,main,outputs}.tf terraform.tfvars .terraform.lock.hcl tests/eventos.tftest.hcl
  rutas/    {backend,versions,providers,variables,main,outputs}.tf .terraform.lock.hcl tests/rutas.tftest.hcl
  INVENTARIO.md
scripts/
  build-lambda-zips.sh, check-platform.sh (copia de Multimedia), run-init.sh, smoke-gateway-auditoria.sh,
  crear-reporte-y-auditar.sh (manual, MS-001/002), tests/run.sh + fixtures
.github/workflows/provision.yml, deprovision.yml
README.md                                     sección AWS; CLAUDE.md con comandos y gotchas
```

## 6. Modelo de datos

No cambia: `audit_events`, `audit_chain_state`, los triggers append-only y los permisos de `001_audit_store.sql`.

El init añade, antes del script y como administrador, la creación o sincronización idempotente de los roles. Para cada rol:

- `CREATE ROLE ... LOGIN` si no existe;
- si existe, `ALTER ROLE ... PASSWORD`;
- sin `CREATEDB`, `CREATEROLE` ni `SUPERUSER`.

Las contraseñas salen de los secretos `-db-writer` y `-db-reader`, y el script vuelve a aplicar los `GRANT`.

## 7. Contratos / interfaces

### 7.1 Evento recibido (cuerpo del mensaje SQS = sobre completo de EventBridge, sin transformador)

```json
{ "version": "0", "id": "…", "source": "urban-alert.reportes", "detail-type": "ReporteCreado",
  "time": "…", "detail": { "idEvento": "…", "reporte": { "id": "…", "estado": "REPORTADO",
  "idCoordenada": "…", "idUsuario": "…", "fecha": "2026-10-11T15:30:00Z" } } }
```

- `detail` se deserializa a `ReporteCreadoEvent`: camelCase, sin distinguir mayúsculas, y enums con `CodigosEnum.ConfigurarMensajeria`.
- El registro resultante es idéntico al del consumidor de MassTransit, con `correlationId = null`.
- Un `source` o `detail-type` distinto, o un campo obligatorio vacío o con `Guid.Empty`, es un fallo del elemento: va a la DLQ tras 5 intentos y deja un log de error con el `messageId`.

### 7.2 API publicada

| Ruta del gateway | Autorización | Destino en la app (`Gateway:PathBase=/api/v1`) |
|---|---|---|
| `GET /api/v1/auditoria/reportes/{reportId}` | `NONE` | `GET /auditoria/reportes/{reportId}` |
| `GET /api/v1/auditoria/reportes/{reportId}/integridad` | `NONE` | `GET /auditoria/reportes/{reportId}/integridad` |

Respuestas como hoy: 200, 400 si `limit` o `cursor` no son válidos, y 404 si el id no es un GUID. Con el modo `Ninguna` no hay 401 ni 403, y un reporte sin eventos responde 200 con la lista vacía.

### 7.3 Configuración en la nube

| Clave (variable de entorno) | api | registro | init | Local (por defecto) |
|---|---|---|---|---|
| `Autenticacion__Modo` | `Ninguna` | — | — | `Cognito` |
| `Gateway__ExigirClave` | `false` | — | — | `true` |
| `Gateway__PathBase` | `/api/v1` | — | — | vacío |
| `ConnectionStrings__AuditReaderSecretArn` | ARN `-db-reader` | — | ARN | — |
| `ConnectionStrings__AuditWriterSecretArn` | — | ARN `-db-writer` | ARN | — |
| `Init__AdminSecretArn` | — | — | ARN del secreto de RDS | — |
| `Logging__Console__FormatterName` | `json` | `json` | `json` | texto |
| `RabbitMq__Enabled` | `false` | `false` | `false` | según entorno |
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | `Production` | `Production` | `Production` | `Development` |

Las cadenas de conexión de los secretos llevan `SSL Mode=Require;GSS Encryption Mode=Disable;Maximum Pool Size=2;Minimum Pool Size=0`.

### 7.4 Funciones

| Función | Handler | Memoria / timeout | Concurrencia |
|---|---|---|---|
| `urban-alert-auditoria-api` | `Auditoria.API` (assembly ejecutable) | 512 MB / 15 s | sin reservar (límite de cuenta, D-010) |
| `urban-alert-auditoria-registro` | `Auditoria.Lambda::Auditoria.Lambda.RegistroHandler::ManejarAsync` | 512 MB / 30 s | ≤ 2 por el mapeo |
| `urban-alert-auditoria-init` | `Auditoria.Lambda::Auditoria.Lambda.InitHandler::ManejarAsync` | 512 MB / 60 s | sin límite (la invoca solo Provision) |

## 8. Decisiones

### D-001: Runtime gestionado `dotnet10` con paquetes .zip
- **Decisión**: runtime gestionado `dotnet10`, x86_64, publicado *framework-dependent* en .zip. Sin ECR ni imagen.
- **Alternativas**: imagen de contenedor (requiere ECR y cambia de patrón); `provided.al2023` con Native AOT (Npgsql y ASP.NET MVC con reflexión complican el AOT); arm64 (algo más barato, pero sin ventaja apreciable a este volumen, y Multimedia usa x86_64).
- **Motivo**: es el mismo patrón que Multimedia y sin recursos extra. `dotnet10` existe como runtime gestionado desde enero de 2026.

### D-002: Dos paquetes y tres funciones
- **Decisión**: `api.zip`, que es la publicación de `src/API` con el host de Lambda (`AddAWSLambdaHosting(LambdaEventSource.HttpApi)`, sin efecto fuera de Lambda), y `funciones.zip`, que es la publicación de `src/Lambda` con los handlers de registro e init.
- **Alternativas**: una sola función que distinga el tipo de evento (mezcla permisos y concurrencia); el consumidor dentro del host ASP.NET Core (arranque más lento y permisos de escritor en la API).
- **Motivo**: permisos mínimos por función (RNF-001), concurrencia distinta para cada una (RNF-002), y la API se queda como está.

### D-003: Contrato del evento copiado en el repo
- **Decisión**: se copian `ReporteCreadoEvent`, `ReporteEventoDto` y `EstadoReporte` con los **mismos namespaces** que en Reportes (`Application.Reportes.Eventos`, `Domain.Reportes`). Se elimina la referencia al proyecto hermano.
- **Alternativas**: hacer checkout de `urban-alert-reportes` en CI (acopla los builds y necesita un token); un paquete NuGet compartido (infraestructura nueva).
- **Motivo**: el repo debe compilar solo, en CI. Mantener los namespaces conserva el URN de MassTransit, así que el consumo local por RabbitMQ sigue funcionando.

### D-004: SQS entre la regla y la función
- **Decisión**: cola estándar con DLQ (`maxReceiveCount = 5`, 14 días), mapeo con `maximum_concurrency = 2`, lote de 10 procesado en serie, `ReportBatchItemFailures` y visibilidad de 180 s (6 × 30 s).
- **Alternativas**: la regla invoca directamente la Lambda (asíncrono, sin control de concurrencia, solo con DLQ en el destino); cola FIFO (EventBridge admite FIFO, pero no hace falta orden: la cadena es por llegada).
- **Motivo**: la cadena global se serializa con `FOR UPDATE`, y limitar la concurrencia evita contención y agotar conexiones. Los fallos quedan retenidos (RF-005).

### D-005: Regla y destino en un stack propio, aplicado tras el init
- **Decisión**: el stack `eventos` contiene la regla, el destino y la política de la cola. Se aplica después del init y se destruye antes que las funciones.
- **Alternativas**: dentro de `base` (entregaría eventos antes de que exista el esquema; sobrevivirían en la cola, pero gastarían intentos) o dentro de `servicio`.
- **Motivo**: RF-016 y HU-5. Así no se pierden intentos y el desmontaje es limpio.

### D-006: Autorización por configuración (`Autenticacion:Modo`)
- **Decisión**: `Cognito` es el valor por defecto y el comportamiento actual. `Ninguna` registra un esquema de autenticación que da un principal fijo (rol `admin`) y un `IAuditAuthorizationService` que no consulta la base de Reportes. Los controladores no cambian. `Autenticacion:ModoDesarrollo` sigue exigiendo `Development`.
- **Alternativas**: quitar `[Authorize]` y la autorización del código (rompe el comportamiento local, RF-020); `ModoDesarrollo` en la nube (exige `Development` y permite elegir la identidad).
- **Motivo**: es la decisión del usuario (sin Cognito ni autorización), con el mínimo cambio y reversible cuando haya Cognito.

### D-007: Cabecera del gateway y prefijo de ruta en la nube
- **Decisión**: `Gateway:ExigirClave=false` desactiva el filtro `X-Urban-Gateway-Key`. `Gateway:PathBase=/api/v1` aplica `UsePathBase`, de modo que `/api/v1/auditoria/reportes/...` llega a `auditoria/reportes/...`.
- **Alternativas**: reescribir la ruta en el gateway (las integraciones `AWS_PROXY` de Lambda no admiten `overwrite:path` ni cabeceras fijas); cambiar la ruta del controlador (rompe la convención local).
- **Motivo**: con Lambda, el único que puede invocar es el gateway (`aws_lambda_permission`), y eso cumple RF-008 mejor que un secreto compartido.

### D-008: Credenciales desde Secrets Manager al arrancar
- **Decisión**: si existe `ConnectionStrings:<Nombre>SecretArn`, se lee ese secreto al construir la configuración y se usa su `connection_string`. Se lee una vez por instancia de ejecución.
- **Alternativas**: inyectar la cadena en variables de entorno (la contraseña quedaría visible en la consola de Lambda y en el plan); extensión de parámetros y secretos de Lambda (añade una capa).
- **Motivo**: RF-011, igual que Multimedia (`MONGODB_URI_SECRET_ARN`).

### D-009: Secretos y roles separados
- **Decisión**: hay tres secretos, `-db-writer`, `-db-reader` y el maestro que gestiona RDS. El rol `-api` lee `-db-reader`, `-registro` lee `-db-writer` y además `sqs:ReceiveMessage`, `DeleteMessage` y `GetQueueAttributes` sobre su cola, y `-init` lee los tres. Los tres roles tienen `AWSLambdaVPCAccessExecutionRole`, o su equivalente en línea, y logs en su grupo.
- **Motivo**: RNF-001 y RF-010: la API no puede escribir.

### D-010: RDS como la de Reportes
- **Decisión**: PostgreSQL 17, `db.t4g.micro`, 20→50 GB gp3, una zona, 7 días de copias, `manage_master_user_password`, protección contra borrado y copia final.
- **Motivo**: es la decisión del usuario (RDS propia), tiene el coste mínimo y sigue un patrón ya probado. Conexiones: 2 × 2 (registro) + 10 × 2 (api) + 2 (init) = 26 ≤ 40 (RNF-002).
- **Ajuste (T003, 2026-10-11)**: la cuenta tiene, como supone Multimedia (su D-008), el límite de 10 ejecuciones concurrentes de las cuentas nuevas, y Lambda exige dejar 10 sin reservar: la API **no reserva concurrencia**. Su tope real es el de la cuenta (10), así que el cálculo de conexiones no cambia. Si se sube el límite a ≥ 20, se puede reservar 10.

### D-011: Logs
- **Decisión**: se usa el formateador JSON de consola de .NET (`Logging__Console__FormatterName=json`) en las tres funciones, y `HttpLogging` en la API con método, ruta, código y duración, sin cabeceras ni cuerpos, como Reportes. El registro deja un log por evento con `EventId`, `ReportId` y `MessageId`.
- **Motivo**: RF-012.

### D-012: Paquetes deterministas
- **Decisión**: `scripts/build-lambda-zips.sh` publica con `-p:Deterministic=true -p:ContinuousIntegrationBuild=true`, normaliza las fechas (`touch -t 198001010000`) y comprime con `zip -X` en orden fijo. El `source_code_hash` sale de `filebase64sha256`.
- **Motivo**: RNF-005 y P10: relanzar el mismo commit no redespliega.

## 9. Estrategia de pruebas

| Nivel | Qué | Comando |
|---|---|---|
| Unitarias | Sobre de EventBridge → `ReporteCreadoEvent` (válido, enum numérico o código, `source` o `detail-type` erróneos, campos vacíos); `RegistroHandler` (lote mixto → solo los fallidos en `BatchItemFailures`, sin parar el lote); modo `Ninguna` (200 sin token, sin consultar `CorePostgres`); `Gateway:ExigirClave=false`; `PathBase`; resolución de secretos con cliente simulado; los tests existentes sin cambios | `dotnet test tests/UnitTests/UnitTests.csproj` |
| Integración | `AuditDatabaseInitializer` sobre PostgreSQL vacío (crea roles y esquema; un segundo pase no cambia nada; `audit_reader` no puede insertar); `RegistroHandler` + `AuditStore` reales con el mismo evento dos veces → un solo registro; 20 eventos con 2 en paralelo → integridad correcta | `dotnet test tests/IntegrationTests/IntegrationTests.csproj` (Docker o Podman) |
| Terraform | Un `.tftest.hcl` por stack con `mock_provider`: nombres con el prefijo, SG de RDS solo desde el SG `-lambda`, protección contra borrado, DLQ y redrive, concurrencia del mapeo = 2, patrón exacto de la regla, política de la cola limitada a la regla, rutas exactas y solo `GET`, permiso limitado al prefijo, variables de entorno de §7.3 sin contraseñas | `terraform -chdir=infra/<stack> test` |
| Scripts | `scripts/tests/run.sh` con fixtures (humo, init y check-platform), como en Reportes y Plataforma | `bash scripts/tests/run.sh` |
| Workflows | actionlint, shellcheck | §2 |
| Humo (AWS) | Paso 8 de §4.3 | en Provision |
| E2E manual | `scripts/crear-reporte-y-auditar.sh <gateway>`: crea un reporte en Reportes, espera hasta 60 s su evento en Auditoria y verifica la integridad (MS-001); con `-n 20`, MS-002 | a mano tras Provision |

## 10. Riesgos y mitigaciones

| Riesgo | Impacto | Mitigación |
|---|---|---|
| R-1: arranque en frío de .NET en VPC (2–4 s) | La primera consulta es lenta | Dentro de los 30 s del gateway. Memoria de 512 MB. SnapStart para .NET si hace falta (fuera de alcance) |
| R-2: evento con formato distinto al previsto | Todos los eventos van a la DLQ | Pruebas con el JSON real del README de Reportes. La DLQ se revisa en el resumen de Provision. El README documenta cómo reprocesar (`start-message-move-task`) |
| R-3: eventos publicados antes de la regla, o durante un Deprovision | Se pierden | Documentado (spec, casos borde). Los reportes existentes no se auditan retroactivamente |
| R-4: borrado lento del SG por las ENI de Lambda | Deprovision tarda 20–40 min | Timeout amplio en el job, como Multimedia |
| R-5: la política adjunta no cubre alguna acción (p. ej. etiquetas del mapeo o de la regla) | Falla el primer Provision | El plan de Terraform muestra el `AccessDenied`. Se corrige la política en `infra/bootstrap` y se pide al usuario que la vuelva a aplicar |
| R-6: secreto o contraseña impresos en el log del workflow | Exposición | Sin `set -x`; `run-init.sh` no imprime la respuesta salvo `ok`. Salidas de Terraform sin `sensitive` crudo. Pruebas que lo verifican |
| R-7: `Amazon.Lambda.AspNetCoreServer` y `UsePathBase` con el payload 2.0 | 404 en la API | Prueba unitaria con un `APIGatewayHttpApiV2ProxyRequest` simulado (`Amazon.Lambda.TestUtilities`) |

## 11. Trazabilidad

| Requisito | Componentes | Pruebas |
|---|---|---|
| RF-001 | `base/sqs.tf`, `eventos` (target y política) | tftest base y eventos; humo (regla y mapeo) |
| RF-002 | `eventos/main.tf` | tftest eventos (patrón, bus, un destino) |
| RF-003 | `EventBridgeReporteCreado`, `RegistroHandler`, contrato copiado (D-003) | unitarias del sobre; integración registro |
| RF-004 | `AuditStore` (sin cambios), mapeo concurrencia 2 | integración (duplicado, paralelo) |
| RF-005 | redrive y DLQ, `ReportBatchItemFailures` | tftest base y servicio; unitaria del lote mixto |
| RF-006 | `rutas/main.tf`, `Gateway:PathBase` | tftest rutas; unitaria de PathBase; humo |
| RF-007 | `Autenticacion:Modo=Ninguna`, `SinAutorizacionService` | unitarias del modo; existentes sin cambios |
| RF-008 | `aws_lambda_permission`, sin URL de función | tftest rutas y servicio |
| RF-009 | `base/rds.tf`, `network.tf` | tftest base |
| RF-010 | `AuditDatabaseInitializer`, `InitHandler`, `run-init.sh` | integración del init; humo (Provision paso 5) |
| RF-011 | `secrets.tf`, `SecretosConexion` | tftest servicio (sin contraseñas en el entorno); unitaria |
| RF-012 | D-011 | unitaria (log por evento); revisión en CloudWatch |
| RF-013 | `infra/*` | tftest; INVENTARIO |
| RF-014 | Recursos solo con prefijo propio; bus por ARN | tftest (nombres); revisión |
| RF-015 | `backend.tf` de cada stack | `validate` |
| RF-016 | `provision.yml` §4.3 | actionlint; primer Provision |
| RF-017 | `deprovision.yml` §4.4 | actionlint; ejecución manual (MS-004) |
| RF-018 | `permissions: id-token`, `concurrency` | actionlint; revisión |
| RF-019 | README, INVENTARIO, `infra/bootstrap/README.md` | revisión en verify |
| RF-020 | Configuración por defecto local; MassTransit sin cambios | todas las pruebas existentes; `docker compose` local |
| RNF-001 | D-009, SG, política de la cola | tftest |
| RNF-002 | D-010 (26 conexiones) | tftest (concurrencias y pool) |
| RNF-003 | lote de 10, ventana de 5 s | E2E manual (MS-001) |
| RNF-004 | 512 MB; consultas con índice existente | humo; E2E |
| RNF-005 | D-012 | segundo Provision (MS-003) |
| RNF-006 | §4.6 | README |
| RNF-007 | triggers de `001_audit_store.sql`, permisos de roles | integración del init |
