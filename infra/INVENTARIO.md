# Inventario de recursos de AWS

Cuenta `348364742283`, región `us-east-2`. Spec 001 (RF-013, RF-014, RF-019). Ningún recurso de este servicio se creó a mano antes del código: no hay importaciones.

## 1. Recursos creados por Terraform

Todos llevan el prefijo `urban-alert-auditoria` y las etiquetas `Project`, `Service`, `ManagedBy` y `Stack`.

| Stack | Estado | Recursos | Deprovision |
|---|---|---|---|
| `base` | `auditoria/base.tfstate` | En la VPC compartida: SG `urban-alert-auditoria-lambda` y `urban-alert-auditoria-rds`, subnet group `urban-alert-auditoria-db`, instancia RDS PostgreSQL 17 `urban-alert-auditoria-db` (y el secreto maestro que gestiona RDS); secretos `urban-alert-auditoria-db-writer` y `urban-alert-auditoria-db-reader`; colas SQS `urban-alert-auditoria-eventos` y `urban-alert-auditoria-eventos-dlq`; roles `urban-alert-auditoria-api`, `-registro` e `-init` (políticas en línea `*-runtime`); grupos de logs `/aws/lambda/urban-alert-auditoria-{api,registro,init}` | Se elimina; queda la copia `urban-alert-auditoria-final-<run_id>` |
| `servicio` | `auditoria/servicio.tfstate` | Funciones Lambda `urban-alert-auditoria-api`, `-registro` e `-init` (y las interfaces de red que Lambda crea en las subredes privadas); mapeo de origen de eventos cola → `-registro` | Se elimina |
| `eventos` | `auditoria/eventos.tfstate` | Regla `urban-alert-auditoria-reporte-creado` en el bus `urban-alert-bus-eventos`, su destino (la cola) y la política de acceso de la cola | Se elimina (el bus no) |
| `rutas` | `auditoria/rutas.tfstate` | En el API Gateway compartido: 1 integración `AWS_PROXY` y las rutas `GET /api/v1/auditoria/reportes/{reportId}` y `GET /api/v1/auditoria/reportes/{reportId}/integridad`; permiso `AllowSharedGatewayAuditoriaRoutes` en la función de la API | Se elimina, el primero |

## 2. Recursos externos de arranque (no gestionados por Terraform)

Terraform los necesita para ejecutarse; los workflows no los tocan. Detalle: [bootstrap/README.md](bootstrap/README.md).

| Recurso | ID en AWS | Notas |
|---|---|---|
| Proveedor OIDC de GitHub | `arn:aws:iam::348364742283:oidc-provider/token.actions.githubusercontent.com` | Compartido |
| Rol de los workflows | ARN en el secreto de organización `AWS_ROLE_ARN` | Compartido; confía en `repo:Unal-Team-C@335834608/*:ref:refs/heads/main` |
| Políticas `github-actions-urban-alertPolicy`, `-terraform`, `-platform`, `-multimedia` | `arn:aws:iam::348364742283:policy/github-actions-urban-alert*` | Compartidas: RDS, secretos, roles, SG, API Gateway, SSM, Lambda, logs de Lambda |
| Política `github-actions-urban-alert-auditoria` | `arn:aws:iam::348364742283:policy/github-actions-urban-alert-auditoria` | **De este repo**: [bootstrap/github-auditoria-policy.json](bootstrap/github-auditoria-policy.json) (SQS, mapeo de eventos, regla del bus) |
| Bucket del estado | `urban-alert-tfstate-348364742283` | Claves `auditoria/{base,servicio,eventos,rutas}.tfstate` |

## 3. Recursos ajenos (otros repos o creados a mano; solo se usan o leen)

| Recurso | Dueño | Uso desde aquí |
|---|---|---|
| VPC, subredes privadas, NAT, API Gateway, VPC Link, parámetros SSM `/urban-alert/plataforma/*` | `Unal-Team-C/urban-alert-plataforma` | Se leen de SSM; se registran rutas en el gateway |
| Bus `urban-alert-bus-eventos` | Creado a mano (ningún repo) | Se crea en él la regla de Auditoria; Provision comprueba que existe |
| Regla de Notificaciones y cola `urban_alert_notificaciones` | Notificaciones (a mano) | Ninguno; no se tocan |
| Publicación de `ReporteCreado` | `Unal-Team-C/urban-alert-reportes` | Origen de los eventos auditados; no se modifica |
