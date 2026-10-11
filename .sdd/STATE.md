# Estado SDD

<!-- Puntero de reanudación. Claude lo actualiza en CADA transición (inicio/fin de tarea, cambio de fase, decisión, bloqueo).
     Editar este archivo = checkpoint (lo registra el hook). Mantenlo corto: el detalle va en specs/ y journal.md.
     fase: constitution | specify | clarify | plan | tasks | implement | verify | done
     estado_tarea: - | en_progreso | hecha | bloqueada -->

feature_activa: 001-migracion-aws
fase: plan
tarea_actual: -
estado_tarea: -

## Siguiente paso

Revisar y aprobar plan.md con el usuario → /sdd-tasks.

## Contexto de trabajo

- Referencias: `../FaaS multimedia/Urban-Alert-multimedia` (patrón Lambda: stacks almacen/base/servicio/rutas, zip determinista, init Lambda), `../Microservicio reportes` (RDS propio, secretos, publica `ReporteCreado` en EventBridge), `../Plataforma compartida AWS` (contrato SSM, docs/integracion.md).
- Bus `arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos` (manual). Evento: source `urban-alert.reportes`, detail-type `ReporteCreado`, detail `{idEvento, reporte{id, estado, idCoordenada, idUsuario, fecha}}`.
- Regla de Notificaciones ya existe (destino SQS `urban_alert_notificaciones`); la de Auditoria (`urban-alert-auditoria-reporte-creado`) la crea este repo.
- Sin AWS CLI, Terraform ni dotnet locales: validar con Podman. Hooks del arnés usan `powershell` (no instalado): protocolo a mano.

## Bloqueos / preguntas abiertas

- ninguno
