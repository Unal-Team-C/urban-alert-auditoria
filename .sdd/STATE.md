# Estado SDD

<!-- Puntero de reanudación. Claude lo actualiza en CADA transición (inicio/fin de tarea, cambio de fase, decisión, bloqueo).
     Editar este archivo = checkpoint (lo registra el hook). Mantenlo corto: el detalle va en specs/ y journal.md.
     fase: constitution | specify | clarify | plan | tasks | implement | verify | done
     estado_tarea: - | en_progreso | hecha | bloqueada -->

feature_activa: 001-migracion-aws
fase: clarify
tarea_actual: -
estado_tarea: -

## Siguiente paso

Resolver RF-002 (nombre de la regla de Auditoria y permisos PutTargets/RemoveTargets) y obtener la aprobación de spec.md; después /sdd-plan.

## Contexto de trabajo

- Referencias: `../FaaS multimedia/Urban-Alert-multimedia` (patrón Lambda: stacks almacen/base/servicio/rutas, zip determinista, init Lambda), `../Microservicio reportes` (RDS propio, secretos, publica `ReporteCreado` en EventBridge), `../Plataforma compartida AWS` (contrato SSM, docs/integracion.md).
- Bus `arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos` (manual). Evento: source `urban-alert.reportes`, detail-type `ReporteCreado`, detail `{idEvento, reporte{id, estado, idCoordenada, idUsuario, fecha}}`.
- Regla de Notificaciones ya existe (destino SQS `urban_alert_notificaciones`); la de Auditoria la crea el usuario a mano.
- Sin AWS CLI, Terraform ni dotnet locales: validar con Podman. Hooks del arnés usan `powershell` (no instalado): protocolo a mano.

## Bloqueos / preguntas abiertas

- Nombre de la regla de Auditoria y si la política adjunta incluye `events:PutTargets`/`RemoveTargets`.
