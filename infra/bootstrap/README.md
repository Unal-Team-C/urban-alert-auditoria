# Recursos de arranque de Auditoria

Terraform no gestiona estos recursos: los necesita para poder ejecutarse, y el workflow de desaprovisionamiento no debe borrarlos (spec 001, RF-014 y RF-019).

Cuenta `348364742283`, región `us-east-2`. El JSON de esta carpeta es la copia versionada de lo configurado en AWS: si se cambia la política en la consola, hay que actualizar aquí el JSON, y al revés.

## Lo que ya existe (compartido con los demás repos)

| Recurso | Dónde está documentado |
|---|---|
| Proveedor OIDC de GitHub | [urban-alert-geoespacial/infra/bootstrap](https://github.com/Unal-Team-C/urban-alert-geoespacial/tree/main/infra/bootstrap) |
| Rol de los workflows (`github-actions-…`) y su confianza `repo:Unal-Team-C@335834608/*:ref:refs/heads/main` | ídem; cubre también este repo |
| Políticas `github-actions-urban-alertPolicy` y `github-actions-urban-alert-terraform` | ídem: RDS (`rds:*`), secretos `urban-alert-*`, roles `urban-alert-*`, KMS vía RDS y Secrets Manager, grupos de seguridad |
| Política `github-actions-urban-alert-platform` | [urban-alert-plataforma/infra/bootstrap](https://github.com/Unal-Team-C/urban-alert-plataforma/tree/main/infra/bootstrap): API Gateway, SSM `/urban-alert/*`, `elasticloadbalancing:Describe*` |
| Política `github-actions-urban-alert-multimedia` | [Urban-Alert-multimedia/infra/bootstrap](https://github.com/Unal-Team-C/Urban-Alert-multimedia/tree/main/infra/bootstrap): `lambda:*` sobre `function:urban-alert-*`, `iam:PassRole` a Lambda de `role/urban-alert-*`, logs `/aws/lambda/urban-alert-*`. Este repo la reutiliza para sus funciones |
| Bucket del estado `urban-alert-tfstate-348364742283` | ídem Geoespacial. Este repo usa las claves `auditoria/*.tfstate` |
| Secreto `AWS_ROLE_ARN` | Secreto **de la organización** `Unal-Team-C`, visible para este repo. No hay variables de repo: la región `us-east-2` está fija en los workflows |
| Bus de EventBridge `urban-alert-bus-eventos` | `arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos`. Creado a mano; **ningún repo lo versiona**. Este repo solo crea su regla `urban-alert-auditoria-reporte-creado` en él |

## Pasos manuales (una sola vez, antes del primer Provision)

### 1. Política de Auditoria en el rol de GitHub (hecho el 2026-10-11)

Las políticas compartidas no permiten crear colas SQS, conectar una cola con una función (mapeo de origen de eventos) ni gestionar reglas de EventBridge. Lo que falta está en [github-auditoria-policy.json](github-auditoria-policy.json).

Con un usuario con permisos de IAM, sustituyendo `<rol-de-github>` por el nombre del rol de `AWS_ROLE_ARN` (lo que va después de `role/`):

```bash
aws iam create-policy --policy-name github-actions-urban-alert-auditoria \
  --policy-document file://infra/bootstrap/github-auditoria-policy.json
aws iam attach-role-policy --role-name <rol-de-github> \
  --policy-arn arn:aws:iam::348364742283:policy/github-actions-urban-alert-auditoria
```

Para actualizarla tras un cambio del JSON (se conservan hasta 5 versiones):

```bash
aws iam create-policy-version --set-as-default \
  --policy-arn arn:aws:iam::348364742283:policy/github-actions-urban-alert-auditoria \
  --policy-document file://infra/bootstrap/github-auditoria-policy.json
```

| Bloque | Para qué |
|---|---|
| `AuditoriaQueues` | Crear, configurar y borrar las colas `urban-alert-auditoria-eventos` y `-eventos-dlq`, y su política de acceso |
| `AuditoriaEventSourceMapping` | Conectar la cola con la función de registro. AWS no admite restringir estas acciones a una función concreta, por eso `"*"` |
| `AuditoriaRule` | Crear y borrar la regla `urban-alert-auditoria-reporte-creado` del bus y su destino. Limitado a reglas con el prefijo `urban-alert-auditoria-`: la regla de Notificaciones queda fuera |
| `DescribeSharedEventBus` | Comprobar que el bus existe antes de cambiar nada (spec RF-016) |

### 2. Límite de concurrencia de Lambda

Se supone el límite de las cuentas nuevas: 10 ejecuciones concurrentes en total, compartidas con Multimedia. Por eso ninguna función reserva concurrencia (plan D-010). La de registro está limitada a 2 por el mapeo de la cola.

```bash
aws lambda get-account-settings --query 'AccountLimit.ConcurrentExecutions'
```

### 3. Comprobar

Lanzar Provision con un push a `main`, con la plataforma y el bus ya existentes.

- Si falla en `configure-aws-credentials`, el problema es la confianza OIDC del rol o el secreto de la organización.
- Si falla con `AccessDenied` sobre `sqs:`, `lambda:*EventSourceMapping` o `events:`, falta o está desactualizado el paso 1. El siguiente push retoma desde el estado.
- Si falla con `AccessDenied` sobre `lambda:`, `iam:PassRole` o `logs:` de `/aws/lambda/`, falta la política de Multimedia en el rol.
