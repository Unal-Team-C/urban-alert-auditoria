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
