# UrbanAlert.Auditoria

Servicio .NET 10 para auditar los eventos de `UrbanAlert.Reportes`: por MassTransit/RabbitMQ en local y por Amazon EventBridge en AWS. Guarda eventos en PostgreSQL append-only, calcula una cadena SHA-256 global e idempotente por `eventId`, y ofrece API para consultar el historial e integridad.

En AWS corre como **funciones Lambda** con su propia RDS PostgreSQL: ver [Despliegue en AWS](#despliegue-en-aws).

## Estructura de la solución

```
src/
  Domain/          Lógica pura: cálculo e integridad del hash de la cadena de auditoría.
                    Domain/Reportes: copia del enum EstadoReporte de Reportes (contrato del evento).
  Application/     DTOs, interfaces de los puertos (IAuditStore, IAuditArchive, IAuditAuthorizationService),
                    el mapeo de eventos de Reportes al modelo de auditoría y la lectura del sobre de EventBridge.
                    Application/Reportes/Eventos: copia de ReporteCreadoEvent (mismo namespace que en Reportes).
  Infrastructure/   Implementaciones: PostgreSQL (Npgsql), inicialización de la base, secretos de conexión,
                    archivado S3 y el consumidor MassTransit.
  API/             Host ASP.NET Core: controladores, autenticación y Program.cs (también corre en Lambda).
  Lambda/          Handlers de las funciones de registro (SQS) e inicialización de la base.
tests/
  UnitTests/        Pruebas unitarias (mocks, sin dependencias externas).
  IntegrationTests/ Pruebas con PostgreSQL real vía Testcontainers.
deploy/docker/      Dockerfile y compose locales.
infra/              Terraform de AWS (base, servicio, eventos, rutas) y recursos de arranque.
scripts/            Paquetes de Lambda, comprobaciones, inicialización, humo y prueba de extremo a extremo.
```

## Flujo auditado

El consumidor usa el tipo .NET `Application.Reportes.Eventos.ReporteCreadoEvent`, publicado actualmente por `UrbanAlert.Reportes`. La auditoría normaliza los campos disponibles (`IdEvento`, `Id`, `IdUsuario`, `IdCoordenada`, `Estado` y `Fecha`). El mensaje actual no trae `CorrelationId`, por lo que queda nulo en el registro. Hoy solo se audita la creación: Reportes todavía no publica eventos para sus cambios de estado, asignación, nivel de emergencia, rechazo o eliminación.

La API expone:

- `GET /auditoria/reportes/{reportId}?limit=100&cursor=0`
- `GET /auditoria/reportes/{reportId}/integridad` (solo gestor/admin)
- `GET /health`

La API valida JWT de Cognito y consulta la tabla `"Reportes"` de PostgreSQL. Ciudadanos solo consultan reportes cuyo `IdUsuario` coincide con su `sub`; gestores, aquellos cuyo `IdResponsable` coincide; admin puede consultar cualquier reporte.

## Preparación

1. Crear en la base de auditoría los roles `audit_writer` y `audit_reader` con credenciales gestionadas fuera del repositorio. Aplicar `src/Infrastructure/Persistencia/Migraciones/001_audit_store.sql` como administrador. El escritor solo inserta eventos y actualiza la cabeza de la cadena; el lector solo consulta. La tabla de eventos rechaza `UPDATE`, `DELETE` y `TRUNCATE` mediante triggers.
2. Ninguna credencial va en `appsettings*.json`: esos archivos solo tienen la estructura de claves con valores vacíos o no sensibles. Las credenciales reales se inyectan por entorno:
   - **Local, `dotnet run`:** con [User Secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) (ver [Desarrollo local](#desarrollo-local)).
   - **Local, `docker compose`:** con un `.env` gitignorado junto al compose (ver [Compose de este servicio](#compose-de-este-servicio)).
   - **Desplegado (staging/producción):** variables de entorno inyectadas por la plataforma desde un secret manager (p. ej. AWS Secrets Manager o SSM Parameter Store), nunca archivos en el repo. Las claves a proveer son:

```text
ConnectionStrings__AuditWriter=<PostgreSQL con rol audit_writer>
ConnectionStrings__AuditReader=<PostgreSQL con rol audit_reader>
ConnectionStrings__CorePostgres=<PostgreSQL del servicio UrbanAlert.Reportes, solo lectura>
Cognito__Issuer=<issuer del User Pool>
Cognito__AppClientId=<App Client ID>
Gateway__SharedSecret=<secreto compartido con API Gateway>
RabbitMq__Enabled=true
RabbitMq__Host=<host RabbitMQ>
RabbitMq__VirtualHost=/
RabbitMq__Username=<usuario>
RabbitMq__Password=<secreto>
RabbitMq__Queue=q_auditoria_dotnet
AuditArchive__Bucket=<bucket Object Lock, opcional>
AuditArchive__ObjectLockRetentionDays=2555
AuditArchive__ServiceUrl=<endpoint S3 compatible, opcional>
AWS_REGION=us-east-1
```

Si `AuditArchive__Bucket` está vacío, el archivado queda desactivado. Para activarlo, el bucket debe tener S3 Object Lock habilitado y el rol de ejecución necesita permisos de lectura de metadatos y escritura con retención (`s3:GetObject`, `s3:PutObject` y `s3:PutObjectRetention`). El SDK obtiene credenciales desde la cadena estándar de AWS; en despliegue se recomienda un rol de ejecución en lugar de claves estáticas.

Por cada evento, el servicio consulta primero `events/{yyyy-MM-dd}/{eventId}.json`; si no existe, archiva el sobre completo, el hash anterior y el hash calculado con modo `COMPLIANCE`. La fecha de retención se calcula desde el momento del archivado. Si S3 falla después del commit de PostgreSQL, el mensaje se reencola; la clave idempotente evita añadir otro evento y el reintento vuelve a intentar el archivo.

3. Ejecutar desde la raíz del repositorio:

```powershell
dotnet run --project src/API/Auditoria.API.csproj
dotnet test tests/UnitTests/UnitTests.csproj
dotnet test tests/IntegrationTests/IntegrationTests.csproj
```

`UnitTests` solo usa mocks. `IntegrationTests` usa Testcontainers y requiere Docker disponible; se inicia un PostgreSQL temporal y se aplica `001_audit_store.sql`.

## Desarrollo local

`src/API/appsettings.Development.json` solo trae configuración no sensible (logging, `RabbitMq:Enabled/Host/VirtualHost/Queue`). Las cadenas de conexión y las credenciales de RabbitMQ se cargan con **User Secrets** (`UserSecretsId` ya está en `src/API/Auditoria.API.csproj`), que ASP.NET Core lee automáticamente en `Development` y que viven fuera del repo, en tu perfil de usuario. `src/API/Properties/launchSettings.json` levanta la API en `http://localhost:5040`.

Preparar la base una sola vez (o tras borrar el volumen) contra el PostgreSQL de `UrbanAlert.Reportes`:

```bash
psql_reportes() { docker exec -i urbanalert-reportes-postgres psql -v ON_ERROR_STOP=1 -U urbanalert "$@"; }
psql_reportes -d postgres <<'SQL'
CREATE DATABASE urbanalert_auditoria;
CREATE ROLE audit_writer LOGIN PASSWORD 'audit_writer';
CREATE ROLE audit_reader LOGIN PASSWORD 'audit_reader';
CREATE ROLE reportes_reader LOGIN PASSWORD 'reportes_reader';
SQL
psql_reportes -d urbanalert_auditoria < src/Infrastructure/Persistencia/Migraciones/001_audit_store.sql
psql_reportes -d urbanalert_reportes -c 'GRANT SELECT ON "Reportes" TO reportes_reader;'
```

La tabla `"Reportes"` la crean las migraciones de Reportes, así que esa API debe haberse ejecutado al menos una vez. Luego, cargar esas mismas credenciales en User Secrets (una sola vez por máquina; `dotnet` las guarda fuera del repo):

```bash
cd src/API
dotnet user-secrets set "ConnectionStrings:AuditWriter" "Host=localhost;Port=5432;Database=urbanalert_auditoria;Username=audit_writer;Password=audit_writer"
dotnet user-secrets set "ConnectionStrings:AuditReader" "Host=localhost;Port=5432;Database=urbanalert_auditoria;Username=audit_reader;Password=audit_reader"
dotnet user-secrets set "ConnectionStrings:CorePostgres" "Host=localhost;Port=5432;Database=urbanalert_reportes;Username=reportes_reader;Password=reportes_reader"
dotnet user-secrets set "RabbitMq:Username" "urbanalert"
dotnet user-secrets set "RabbitMq:Password" "urbanalert"
```

Luego basta con `dotnet run --project src/API/Auditoria.API.csproj`. En Development la referencia de la API está en `http://localhost:5040/scalar/v1`; los endpoints de `/auditoria` siguen exigiendo un JWT de Cognito, salvo en modo de desarrollo.

### Modo de desarrollo sin Cognito

Para consultar la auditoría mientras no hay inicio de sesión:

```bash
Autenticacion__ModoDesarrollo=true dotnet run --project src/API/Auditoria.API.csproj
```

Solo se permite con `ASPNETCORE_ENVIRONMENT=Development`: en otro entorno el servicio no arranca. Sin header `Authorization`, la identidad se toma de los headers:

| Header | Valores | Por defecto |
|---|---|---|
| `X-Usuario-Id` | GUID del usuario (el `idUsuario` de los reportes) | `018f4c2a-0000-7000-8000-000000000001`, el admin de los datos semilla de Usuarios |
| `X-Usuario-Rol` | `ciudadano`, `gestor` o `admin` | `admin` |

Se aplican las mismas reglas de acceso que con Cognito: un ciudadano solo ve sus reportes y un gestor, los que tiene asignados. Un header inválido responde 401 con el motivo. Con `Authorization: Bearer` se valida el token de Cognito como siempre. En Scalar ambos headers aparecen como parámetros de cada endpoint.

Cualquiera que llegue a la API puede elegir su identidad, así que no se debe activar en un entorno expuesto con datos reales.

La imagen se construye con `deploy/docker/Dockerfile`. MassTransit crea y enlaza la cola `q_auditoria_dotnet` al exchange del tipo `ReporteCreadoEvent`; los errores persistentes siguen la política de reintentos/error queue de MassTransit.

### Compose de este servicio

`deploy/docker/docker-compose.yml` levanta únicamente la API de Auditoría y su propio PostgreSQL (`urbanalert-auditoria-postgres`, puerto `5433` para no chocar con el de Reportes), bajo el nombre de proyecto `urbanalert-auditoria` (ver comentario en el propio archivo: sin esto, dos compose distintos guardados en una carpeta `docker/` pueden pisarse contenedores entre sí). No incluye RabbitMQ ni el PostgreSQL de Reportes: `RabbitMq__Enabled` queda en `false` y `ConnectionStrings__CorePostgres` vacío, así que la autorización por reporte no funcionará hasta configurar esa cadena apuntando al PostgreSQL de `UrbanAlert.Reportes`.

Las credenciales del postgres del compose salen de `deploy/docker/.env` (gitignorado); copiar `deploy/docker/.env.example` y ajustar `AUDITORIA_DB_PASSWORD` antes del primer `up`:

```bash
cp deploy/docker/.env.example deploy/docker/.env
# editar deploy/docker/.env con la contraseña real
docker compose -f deploy/docker/docker-compose.yml up -d --build
```

La API queda en `http://localhost:5040` (modo de desarrollo sin Cognito activado) y aplica `001_audit_store.sql` a mano la primera vez:

```bash
docker exec -i urbanalert-auditoria-postgres psql -v ON_ERROR_STOP=1 -U urbanalert -d urbanalert_auditoria \
  < src/Infrastructure/Persistencia/Migraciones/001_audit_store.sql
```

## Integración y alcance

El consumidor se conecta únicamente al tipo MassTransit de Reportes .NET. En el workspace no existe un productor Geoespacial .NET que publique eventos para esta auditoría.

La cobertura seguirá incompleta hasta que los handlers .NET publiquen eventos para transiciones, asignación, nivel de emergencia, rechazo y eliminación. El contrato actual no incluye `CorrelationId`, municipio ni coordenadas geográficas; el consumidor no fabrica estos datos. Además, mientras no haya autenticación, `IdUsuario` lo envía el cliente de Reportes (o se genera uno genérico si no lo envía), así que el filtro de propietario no corresponde a un `sub` real de Cognito.

El archivo de auditoría, la consulta de integridad y el archivado opcional S3 Object Lock están portados. La política de retención y el bucket deben configurarse en AWS; este servicio no crea ni configura el bucket.

El detalle del flujo, el contenido normalizado del evento y las brechas actuales está en [docs/funcionamiento-dotnet.md](docs/funcionamiento-dotnet.md).

## Despliegue en AWS

Cuenta `348364742283`, región `us-east-2`. Spec, plan y decisiones en [specs/001-migracion-aws](specs/001-migracion-aws/); inventario de recursos en [infra/INVENTARIO.md](infra/INVENTARIO.md); recursos de arranque en [infra/bootstrap/README.md](infra/bootstrap/README.md).

El servicio corre en **tres funciones Lambda** (runtime `dotnet10`) en la red privada de la plataforma compartida [`urban-alert-plataforma`](https://github.com/Unal-Team-C/urban-alert-plataforma), con su **propia RDS PostgreSQL 17**. Recibe los eventos de Reportes del **bus de EventBridge** compartido y publica su API **solo** por el API Gateway compartido.

```text
Reportes ─PutEvents─► bus urban-alert-bus-eventos (manual)
                        ├─ regla de Notificaciones ─► urban_alert_notificaciones      (no se toca)
                        └─ regla urban-alert-auditoria-reporte-creado ─► SQS urban-alert-auditoria-eventos ─(5 intentos)─► DLQ
                                                                          │ lote 10, ≤ 2 invocaciones a la vez
                                                     Lambda urban-alert-auditoria-registro ─TLS─► RDS (audit_writer)
Cliente ─HTTPS─► API Gateway ─ GET /api/v1/auditoria/reportes/{id}[/integridad] ─► Lambda urban-alert-auditoria-api ─TLS─► RDS (audit_reader)
Provision ─► Lambda urban-alert-auditoria-init ─TLS─► RDS (usuario maestro): roles y 001_audit_store.sql
```

| Stack | Estado | Qué crea |
|---|---|---|
| `infra/base` | `auditoria/base.tfstate` | RDS, secretos `-db-writer`/`-db-reader`, colas y DLQ, grupos de seguridad, roles, logs |
| `infra/servicio` | `auditoria/servicio.tfstate` | Las 3 funciones y la conexión cola → `registro` |
| `infra/eventos` | `auditoria/eventos.tfstate` | La regla del bus, su destino (la cola) y la política de la cola |
| `infra/rutas` | `auditoria/rutas.tfstate` | 2 rutas en el gateway compartido y el permiso de invocación |

### Cómo llamar a la API

Por la URL del gateway (salida `gateway_url`, hoy `https://aotsp8k5ad.execute-api.us-east-2.amazonaws.com`), sin token ni cabeceras:

```bash
curl https://<api-id>.execute-api.us-east-2.amazonaws.com/api/v1/auditoria/reportes/<idReporte>?limit=100
curl https://<api-id>.execute-api.us-east-2.amazonaws.com/api/v1/auditoria/reportes/<idReporte>/integridad
```

- Por ahora no hay autenticación de clientes ni autorización por reporte (decisión del usuario, spec RF-007): cualquiera que llegue al gateway consulta cualquier reporte, igual que la API de Reportes. Un reporte sin eventos responde 200 con `items` vacío.
- Solo esas dos rutas existen: `/health`, Scalar u OpenAPI responden 404 en el gateway.
- La cabecera `X-Urban-Gateway-Key` no se usa en AWS: con una integración Lambda el gateway no puede añadir cabeceras, y la función solo admite invocaciones del gateway compartido (plan D-007).

### Configuración: local y AWS

Todo lo que cambia en la nube se elige por configuración; sin ella, el comportamiento es el local.

| Clave | Local (por defecto) | AWS |
|---|---|---|
| `Autenticacion:Modo` | `Cognito` (con `Autenticacion:ModoDesarrollo` en Development) | `Ninguna`: sin Cognito ni lectura de la tabla `Reportes` |
| `Gateway:ExigirClave` | `true` (`X-Urban-Gateway-Key`) | `false` |
| `Gateway:PathBase` | vacío | `/api/v1` (la integración Lambda no reescribe rutas) |
| `ConnectionStrings:<X>SecretArn` | — | ARN del secreto; `connection_string` se lee al arrancar (`AuditReader` en la API, `AuditWriter` en registro) |
| `Init:AdminSecretArn` | — | Secreto maestro de RDS (solo la función de init) |
| `RabbitMq:Enabled` | según entorno | `false` |
| `Logging:Console:FormatterName` | texto | `json` |

Cada petición deja una línea de log con método, ruta, código y duración (`HttpLogging`), sin cabeceras ni cuerpos; cada evento auditado, una línea con `MessageId`, `EventId` y `ReportId`.

### Evento recibido

El destino de la regla no usa transformador: el cuerpo de cada mensaje SQS es el sobre completo de EventBridge.

```json
{ "source": "urban-alert.reportes", "detail-type": "ReporteCreado",
  "detail": { "idEvento": "…", "reporte": { "id": "…", "estado": "REPORTADO", "idCoordenada": "…", "idUsuario": "…", "fecha": "2026-10-11T15:30:00Z" } } }
```

El registro es el mismo que produce el consumidor de MassTransit (`reporte.creado`, versión 1, `correlationId` nulo). Un mensaje con otro `source` o `detail-type`, o sin los campos obligatorios, no se registra y acaba en la cola de fallidos.

Los eventos publicados mientras la regla no existe (antes del primer Provision o durante un Deprovision) se pierden: EventBridge no los guarda.

### Cola de fallidos

Un mensaje que falla 5 veces pasa a `urban-alert-auditoria-eventos-dlq`, donde se conserva 14 días. El resumen de Provision muestra cuántos hay. Para revisarlos y, corregida la causa, devolverlos a la cola principal (con un usuario con permisos sobre SQS):

```bash
aws sqs receive-message --queue-url https://sqs.us-east-2.amazonaws.com/348364742283/urban-alert-auditoria-eventos-dlq --max-number-of-messages 10 --visibility-timeout 0
aws sqs start-message-move-task --source-arn arn:aws:sqs:us-east-2:348364742283:urban-alert-auditoria-eventos-dlq
```

Reprocesar es seguro: el registro es idempotente por `idEvento`.

### Workflows

| Workflow | Cuándo | Qué hace |
|---|---|---|
| [Provision](.github/workflows/provision.yml) | Cada push a `main` | Pruebas unitarias e integración, `terraform fmt/validate/test`, pruebas de scripts, actionlint, shellcheck → paquetes `.zip` deterministas → comprueba plataforma y bus → `base` → `servicio` → init de la base → `eventos` → `rutas` → humo → resumen |
| [Deprovision](.github/workflows/deprovision.yml) | Solo a mano, escribiendo `urban-alert-auditoria` | `rutas` → `eventos` → `servicio` → `base` (copia final de RDS) |

Comparten el grupo de `concurrency` `urban-alert-auditoria-aws`. Después de un Provision se puede comprobar el flujo completo (crea reportes en Reportes y espera su auditoría; no lo ejecuta Provision porque escribe datos):

```bash
scripts/crear-reporte-y-auditar.sh https://<api-id>.execute-api.us-east-2.amazonaws.com -n 20
```

### Restaurar los datos tras reaprovisionar

Procedimiento excepcional, a mano: restaurar `urban-alert-auditoria-final-<run_id>` con `aws rds restore-db-instance-from-db-snapshot` en una instancia temporal y copiar `audit_events` y `audit_chain_state` con `pg_dump`/`pg_restore` desde una máquina dentro de la VPC.

### Costes

Estimación mensual en `us-east-2` con el servicio encendido (730 h):

| Recurso | Mes |
|---|---|
| RDS db.t4g.micro, una zona | ~11,7 USD |
| Almacenamiento gp3 20 GB y copias | ~2,3 USD |
| Secrets Manager (2 propios + el de RDS) | 1,2 USD |
| SQS, Lambda, EventBridge, CloudWatch Logs | ~0 (capa gratuita / por uso) |
| **Total** | **~15,2 USD** |

No incluye la plataforma compartida (~36,6 USD/mes). Desaprovisionado solo queda la copia final de RDS (céntimos).

### Validar en local (Podman, sin instalar nada)

```bash
DOTNET='podman run --rm -v "$PWD":/w -w /w -v ~/.cache/nuget-podman:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0 dotnet'
eval "$DOTNET test tests/UnitTests/UnitTests.csproj"
TF='podman run --rm -v "$PWD":/w -w /w -v ~/.cache/tf-podman:/cache -e TF_PLUGIN_CACHE_DIR=/cache docker.io/hashicorp/terraform:1.16.5'
for s in base servicio eventos rutas; do eval "$TF -chdir=infra/$s init -backend=false" && eval "$TF -chdir=infra/$s validate" && eval "$TF -chdir=infra/$s test"; done
eval "$TF fmt -check -recursive infra"
bash scripts/tests/run.sh
podman run --rm -v "$PWD":/repo -w /repo docker.io/rhysd/actionlint:1.7.7
podman run --rm -v "$PWD":/mnt -w /mnt docker.io/koalaman/shellcheck:v0.10.0 scripts/*.sh scripts/tests/*.sh
```

`apply`, `plan` contra el estado real y `destroy` solo se ejecutan desde los workflows.
