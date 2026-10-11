# Notas: 001-migracion-aws

## Indicaciones técnicas (para /sdd-plan)

- Patrón Lambda de `Urban-Alert-multimedia` (spec 002): stacks `base`/`servicio`/`rutas` con estado en `s3://urban-alert-tfstate-348364742283/auditoria/`, paquete .zip determinista, función de init invocada por Provision, integración `AWS_PROXY` payload 2.0 y `aws_lambda_permission` limitado a `<api-execution-arn>/*/*/api/v1/auditoria/*`.
- RDS como `urban-alert-reportes/infra/base/rds.tf` (PostgreSQL 17, `manage_master_user_password`, `deletion_protection = !allow_destroy`, copia final). Cadenas con `SSL Mode=Require;GSS Encryption Mode=Disable`.
- Con integración Lambda (`AWS_PROXY`) el gateway no reescribe la ruta ni inyecta cabeceras: la app debe aceptar el prefijo `/api/v1/auditoria` y `X-Urban-Gateway-Key` deja de tener sentido en la nube (el permiso de invocación cumple RF-008).
- API: ASP.NET Core sobre Lambda (`Amazon.Lambda.AspNetCoreServer.Hosting`) para conservar el controlador. Comprobar si existe el runtime gestionado `dotnet10`; si no, runtime `provided.al2023` con publicación self-contained.
- Registro: SQS → event source mapping con `ReportBatchItemFailures`, `maximum_concurrency` 2 (cadena global, RNF-002), visibility timeout ≥ 6 × timeout de la función, redrive a DLQ `maxReceiveCount` 5, retención DLQ 14 días. Política de cola: `events.amazonaws.com` con `aws:SourceArn` = ARN de la regla.
- Regla y destino: `aws_cloudwatch_event_rule` `urban-alert-auditoria-reporte-creado` (event_bus_name = ARN del bus) + `aws_cloudwatch_event_target` hacia la cola. Política del rol: `events:PutRule/DeleteRule/DescribeRule/EnableRule/DisableRule/PutTargets/RemoveTargets/ListTargetsByRule/TagResource/UntagResource/ListTagsForResource` sobre `rule/urban-alert-bus-eventos/urban-alert-auditoria-*` + `events:DescribeEventBus` sobre el bus.
- Init: aplicar `001_audit_store.sql` como admin, crear/sincronizar `audit_writer` y `audit_reader` con contraseñas de Secrets Manager.
- Auth en la nube: nuevo modo por configuración (p. ej. `Autenticacion:Modo=Ninguna`) que no exige Cognito ni CorePostgres; sin tocar el comportamiento local.
