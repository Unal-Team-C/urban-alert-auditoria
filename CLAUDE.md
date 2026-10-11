# CLAUDE.md

Servicio de Auditoria de UrbanAlert (.NET 10, ASP.NET Core, Npgsql/PostgreSQL; MassTransit/RabbitMQ en local), extraído del monorepo UrbanAlert y migrado a AWS Lambda con CI/CD en GitHub Actions.

- Local: `docker compose -f deploy/docker/docker-compose.yml up -d` (API + su PostgreSQL) o `dotnet run --project src/API/Auditoria.API.csproj` (Development, :5040). Ver README.
- Pruebas: `dotnet test tests/UnitTests/UnitTests.csproj` y `tests/IntegrationTests` (Testcontainers; requiere Docker/Podman).

AWS (spec `specs/001-migracion-aws/`): cuenta 348364742283, us-east-2. Terraform se aplica solo desde GitHub Actions.

- 3 funciones Lambda `dotnet10` (api, registro, init) sobre la plataforma compartida `Unal-Team-C/urban-alert-plataforma` (contrato SSM `/urban-alert/plataforma/`), patrón de `Urban-Alert-multimedia` (Lambda) y `urban-alert-reportes` (RDS propia).
- Stacks `infra/{base,servicio,eventos,rutas}`, estado en `s3://urban-alert-tfstate-348364742283/auditoria/`. Inventario: `infra/INVENTARIO.md`. Arranque: `infra/bootstrap/` (política `github-actions-urban-alert-auditoria`).
- El bus `urban-alert-bus-eventos` se creó a mano: solo se crea en él la regla `urban-alert-auditoria-reporte-creado`. No tocar la regla ni la cola de Notificaciones.
- El contrato del evento de Reportes está copiado (D-003) con sus namespaces originales: no renombrar (URN de MassTransit).
- Configuración de la nube por claves (README «Configuración: local y AWS»): `Autenticacion:Modo=Ninguna`, `Gateway:ExigirClave=false`, `Gateway:PathBase=/api/v1`, `ConnectionStrings:<X>SecretArn`.
- Workflows: `provision.yml` (push a `main`) y `deprovision.yml` (manual, confirmación `urban-alert-auditoria`). `AWS_ROLE_ARN` es secreto de la organización; región fija en los workflows.

## Validación local (Podman, sin SDK .NET ni Terraform instalados)

- .NET: `podman run --rm -v "$PWD":/w -w /w -v ~/.cache/nuget-podman:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/UnitTests/UnitTests.csproj`.
- Integración: añadir `--network host --security-opt label=disable -v /run/user/501/podman/podman.sock:/var/run/docker.sock -e TESTCONTAINERS_RYUK_DISABLED=true -e TESTCONTAINERS_HOST_OVERRIDE=localhost` y lanzar por clase con `--filter`.
- Terraform: `podman run --rm -v "$PWD":/w -w /w -v ~/.cache/tf-podman:/cache -e TF_PLUGIN_CACHE_DIR=/cache docker.io/hashicorp/terraform:1.16.5 -chdir=infra/<stack> init -backend=false` y luego `validate` / `test`.
- Scripts: `bash scripts/tests/run.sh`; actionlint y shellcheck con los comandos del README.

## Gotchas

- Con Ryuk desactivado quedan contenedores `postgres:17-alpine ... -c fsync=off` tras las pruebas: borrar solo esos.
- Con varios contenedores a la vez, la red de Podman da «Connection reset by peer» intermitentes en las pruebas de integración; por clase pasan. CI usa Docker.
- El pg_hba de la imagen de postgres confía en conexiones locales: no probar contraseñas conectando, sino por el hash de `pg_authid`.
- Handler de Lambda .NET con un solo parámetro: el runtime lo trata como la entrada. Por eso `InitHandler.ManejarAsync(Stream, ILambdaContext)`.
- `APIGatewayHttpApiV2ProxyFunction` creada con `StartupMode.FirstRequest` no tiene logger: en pruebas, usar el constructor con `IServiceProvider`.
- Límite de concurrencia de Lambda de la cuenta: 10 (sin reservas).
- `scripts/build-lambda-zips.sh` en el contenedor del SDK necesita `zip` (apt) y tarda varios minutos la primera vez.
- macOS trae bash 3.2: los scripts no usan funciones de bash 4.

## Desarrollo guiado por especificaciones (SDD)

Este proyecto usa un arnés SDD con persistencia en disco para sobrevivir a interrupciones de sesión.
El protocolo es obligatorio en todas las sesiones:

@.sdd/PROTOCOL.md
