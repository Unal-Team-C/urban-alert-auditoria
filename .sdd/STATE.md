# Estado SDD

<!-- Puntero de reanudación. Claude lo actualiza en CADA transición (inicio/fin de tarea, cambio de fase, decisión, bloqueo).
     Editar este archivo = checkpoint (lo registra el hook). Mantenlo corto: el detalle va en specs/ y journal.md.
     fase: constitution | specify | clarify | plan | tasks | implement | verify | done
     estado_tarea: - | en_progreso | hecha | bloqueada -->

feature_activa: 001-migracion-aws
fase: implement
tarea_actual: T032
estado_tarea: bloqueada

## Siguiente paso

T032: comprobaciones locales en verde (ver journal). Falta el permiso del usuario para publicar la rama aws-provisioning (git push -u origin aws-provisioning) y abrir el PR hacia main; tras el merge, T033 (primer Provision).

## Contexto de trabajo

- Referencias: `../FaaS multimedia/Urban-Alert-multimedia` (patrón Lambda: stacks almacen/base/servicio/rutas, zip determinista, init Lambda), `../Microservicio reportes` (RDS propio, secretos, publica `ReporteCreado` en EventBridge), `../Plataforma compartida AWS` (contrato SSM, docs/integracion.md).
- Bus `arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos` (manual). Evento: source `urban-alert.reportes`, detail-type `ReporteCreado`, detail `{idEvento, reporte{id, estado, idCoordenada, idUsuario, fecha}}`.
- Regla de Notificaciones ya existe (destino SQS `urban_alert_notificaciones`); la de Auditoria (`urban-alert-auditoria-reporte-creado`) la crea este repo.
- Sin AWS CLI, Terraform ni dotnet locales: validar con Podman. Hooks del arnés usan `powershell` (no instalado): protocolo a mano.

- Integración local con Podman: `podman run --rm --network host --security-opt label=disable -v /run/user/501/podman/podman.sock:/var/run/docker.sock -e TESTCONTAINERS_RYUK_DISABLED=true -e TESTCONTAINERS_HOST_OVERRIDE=localhost ... dotnet test tests/IntegrationTests`. Con varios contenedores a la vez hay «Connection reset by peer» intermitentes (red de Podman); por clase con `--filter` pasa. CI decide.
- Mientras haya tests de tareas pendientes que no compilan, verificar en una copia temporal del repo dentro del contenedor (`cp -r /src /w` y borrar esos archivos).
- Con TESTCONTAINERS_RYUK_DISABLED quedan contenedores `postgres:17-alpine ... -c fsync=off` tras las pruebas: borrarlos (solo esos; `postgres` y `rabbitmq-server` sin fsync son del usuario).
- `scripts/build-lambda-zips.sh` en el SDK tarda ~10 min la primera vez (apt zip + restore linux-x64); ejecutar en segundo plano.
- Terraform en Podman: `podman run --rm -v "$PWD":/w -w /w -v ~/.cache/tf-podman:/cache -e TF_PLUGIN_CACHE_DIR=/cache docker.io/hashicorp/terraform:1.16.5 -chdir=infra/<stack> ...` (sin caché, init tarda > 5 min); lanzar en segundo plano.
- AWS_ROLE_ARN es secreto de la organización; el repo no tiene variables (región fija en workflows). Límite de concurrencia de Lambda de la cuenta: 10 (sin reservas).

## Bloqueos / preguntas abiertas

- T032: permiso del usuario para publicar la rama y abrir el PR. Después, el merge (decisión del usuario) lanza el primer Provision (T033).
