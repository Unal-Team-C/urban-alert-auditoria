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
