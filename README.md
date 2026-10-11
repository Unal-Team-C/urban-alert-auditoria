# UrbanAlert.Auditoria

Servicio .NET 10 para auditar mensajes MassTransit de `UrbanAlert.Reportes`. Guarda eventos en PostgreSQL append-only, calcula una cadena SHA-256 global e idempotente por `eventId`, y ofrece API para consultar el historial e integridad.

## Estructura de la solución

```
src/
  Domain/          Lógica pura: cálculo e integridad del hash de la cadena de auditoría.
  Application/     DTOs, interfaces de los puertos (IAuditStore, IAuditArchive, IAuditAuthorizationService)
                    y el mapeo de eventos de Reportes al modelo de auditoría.
  Infrastructure/   Implementaciones: PostgreSQL (Npgsql), archivado S3 y el consumidor MassTransit.
  API/             Host ASP.NET Core: controladores, autenticación y Program.cs.
tests/
  UnitTests/        Pruebas unitarias (mocks, sin dependencias externas).
  IntegrationTests/ Pruebas con PostgreSQL real vía Testcontainers.
deploy/docker/      Dockerfile de despliegue.
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
