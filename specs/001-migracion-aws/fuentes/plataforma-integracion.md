# Guía de integración de un microservicio

Cómo desplegar un microservicio de UrbanAlert en la red compartida y publicarlo **solo** a través del API Gateway compartido. Referencia ejecutable: [`infra/ejemplo-consumidor/`](../infra/ejemplo-consumidor/), que el CI de este repo valida y prueba en cada cambio. Copia de ahí lo que necesites; nunca se aplica desde este repo.

## 0. Requisitos previos

- La plataforma está aprovisionada (workflow Provision de este repo en verde).
- El rol de GitHub de los workflows tiene la política [`github-actions-urban-alert-platform`](../infra/bootstrap/README.md). El rol es compartido, así que tu repo ya puede usarla.
- El servicio se despliega con ECS Express Mode desde los workflows de su propio repo.

## 1. Leer las salidas de la plataforma

La plataforma publica su contrato en SSM Parameter Store, bajo `/urban-alert/plataforma/`:

| Parámetro | Para qué |
|---|---|
| `region` | Región (`us-east-2`) |
| `vpc-id`, `vpc-cidr` | VPC compartida (grupos de seguridad, reglas) |
| `subredes-privadas` | `StringList` con las subredes privadas, en orden de zona |
| `api-id`, `api-endpoint` | Gateway donde registrar las rutas y su URL pública |
| `api-execution-arn` | Para conceder `execute-api:Invoke` sobre rutas internas |
| `vpc-link-id` | Enlace privado del gateway hacia la VPC |
| `vpc-link-sg-id` | Grupo de seguridad del enlace, para usarlo en tus propias reglas |

```hcl
data "aws_ssm_parameter" "subredes_privadas" {
  name = "/urban-alert/plataforma/subredes-privadas"
}
# subnets = split(",", data.aws_ssm_parameter.subredes_privadas.value)
```

```bash
aws ssm get-parameter --name /urban-alert/plataforma/api-endpoint --query Parameter.Value --output text
```

Si la plataforma no existe, la lectura falla con `ParameterNotFound` sobre `/urban-alert/plataforma/...`: aprovisiona antes la plataforma. No copies los valores a mano. Si la plataforma se recrea, cambian, y tu siguiente despliegue los toma de SSM.

## 2. Desplegar el servicio en las subredes privadas

En `aws_ecs_express_gateway_service`, en `network_configuration.subnets` van **todas** las subredes privadas publicadas. Express comparte un ALB interno por VPC (hasta 25 servicios), y ese ALB queda fijado a las zonas del primer servicio que se despliega. Si un servicio usa menos zonas, el siguiente puede fallar.

- Con subredes privadas, Express crea un ALB **interno** y no asigna IP pública a las tareas. Salen a internet (ECR, Secrets Manager, CloudWatch y la URL del gateway) por el NAT de la plataforma.
- Las tareas sueltas que lances con `run-task` (por ejemplo, migraciones) van también en las subredes privadas, con `assignPublicIp=DISABLED`.
- Las bases de datos y demás recursos del servicio se crean en la misma VPC (`vpc-id`) desde el repo del servicio.

## 3. Obtener el endpoint y el listener del servicio

El ALB de Express decide a qué servicio enviar cada petición según la cabecera `Host`. Para cada integración del gateway necesitas dos datos:

- **Endpoint**: `aws_ecs_express_gateway_service.<x>.ingress_paths[0].endpoint`, solo el host (`<servicio>-<id>.ecs.us-east-2.on.aws`).
- **Listener 443 del ALB**: ni Terraform ni la API de Express lo exponen. Usa [`scripts/buscar-listener-express.sh`](../scripts/buscar-listener-express.sh), copiado a tu repo, **después** de que el servicio esté desplegado:

```bash
LISTENER_ARN="$(scripts/buscar-listener-express.sh <cluster> <servicio>)"
```

Por eso las rutas se aplican en un paso posterior al despliegue del servicio: un stack aparte, o el mismo stack aplicado en dos fases. Pasa `express_endpoint` y `express_listener_arn` como variables.

## 4. Registrar las rutas públicas

Sigue la convención de la infraestructura local (`UrbanAlert/infrastructure/README.md`):

| Ruta pública | Servicio |
|---|---|
| `/api/v1/reportes/*` | `/api/v1/Reportes/*` |
| `GET /api/v1/geoespacial/reports` | `/api/v1/geospatial/reports` |
| `GET /api/v1/geoespacial/coordinates/{id}` | `/api/v1/geospatial/coordinates/{id}` |
| `/api/v1/usuarios/*` | `/api/v1/users/*` |
| `/api/v1/auditoria/*` | `/auditoria/*` (con cabecera `X-Urban-Gateway-Key`) |

Cada ruta tiene su integración con estos valores (ver [`main.tf`](../infra/ejemplo-consumidor/main.tf)):

| Atributo | Valor | Por qué |
|---|---|---|
| `integration_type` / `connection_type` | `HTTP_PROXY` / `VPC_LINK` con `vpc-link-id` | El ALB es interno |
| `integration_uri` | ARN del listener 443 | Express solo crea el listener HTTPS |
| `payload_format_version` | `"1.0"` | Obligatorio para un proxy HTTP por VPC Link |
| `tls_config.server_name_to_verify` | endpoint | El certificado del ALB es el del dominio `.on.aws` |
| `request_parameters["overwrite:header.host"]` | endpoint | **Sin esto, el ALB responde 404** |
| `request_parameters["overwrite:path"]` | ruta del servicio, con `$request.path.<param>` si la ruta lleva parámetros | Traducir la ruta pública a la del servicio |
| `request_parameters["overwrite:header.<Nombre>"]` | valor fijo | Cabeceras como `X-Urban-Gateway-Key` (Auditoria). Lee el valor de Secrets Manager, nunca lo escribas en el repo |

Reglas:

- Publica solo los métodos que el servicio expone en público (en Geoespacial, solo `GET`). Las rutas comodín `ANY /api/v1/<servicio>/{proxy+}` van siempre dentro de tu prefijo.
- No registres la ruta `$default` ni rutas fuera de tu prefijo: cualquier ruta no registrada debe seguir respondiendo 404.
- Si intentas registrar una ruta que ya existe en el gateway, la creación falla con `ConflictException` y la ruta existente no cambia.
- No hay autenticación de clientes en el gateway (spec RF-017): las rutas públicas usan `authorization_type = "NONE"`.

## 5. Rutas internas entre servicios (firma IAM)

Los microservicios se llaman entre sí **solo** por la URL del gateway. Las rutas que no deben ser públicas (por ejemplo, `POST /api/v1/geoespacial/coordinates`, que llama Reportes) se registran con `authorization_type = "AWS_IAM"`:

1. El servicio **destino** registra la ruta con `AWS_IAM`.
2. El servicio **que llama** añade a su rol de tarea `execute-api:Invoke` sobre `<api-execution-arn>/$default/<MÉTODO>/<ruta>` (ver [`iam.tf`](../infra/ejemplo-consumidor/iam.tf)).
3. El servicio que llama **firma** cada petición con SigV4 (servicio `execute-api`, región `us-east-2`) usando las credenciales de su rol de tarea, que el SDK de AWS obtiene solo dentro de ECS. Por ejemplo: `botocore.auth.SigV4Auth` en Python, `Aws4RequestSigner`/`AWS4Signer` del SDK en .NET, `AwsV4HttpSigner` del SDK v2 en Java.

Sin firma, o con una identidad sin permiso, el gateway responde **403** y la petición no llega al servicio.

## 6. Aislamiento del servicio

- Desde internet solo se llega al servicio por el gateway: el ALB es interno, está en subredes privadas y las tareas no tienen IP pública.
- Dentro de la VPC, el ALB es alcanzable por otras tareas. Su grupo de seguridad lo crea y gestiona Express, y no se puede restringir al VPC Link (plan D-013). No modifiques a mano ese grupo de seguridad.
- `vpc-link-sg-id` sirve para tus propias reglas, por ejemplo para identificar el tráfico que viene del gateway.

## 7. Cosas a tener en cuenta

- **`Host`**: el servicio recibe su dominio `.on.aws` como `Host`, no el del gateway. `X-Forwarded-Host` no se puede fijar porque es una cabecera reservada. No construyas URL absolutas a partir de `Host`.
- **`Location`** y otras rutas que devuelva el servicio usan su ruta interna (por ejemplo, `/api/v1/geospatial/coordinates/{id}`). Los clientes deben usar los identificadores del cuerpo de la respuesta.
- **Tiempo máximo**: una integración del gateway espera como máximo 30 s.
- **Límite de tasa** por defecto: 50 peticiones por segundo, con ráfagas de 100, para todo el gateway. Si te hace falta más, cámbialo en `infra/plataforma/terraform.tfvars` con un commit.
- **CORS** lo gestiona el gateway (`cors_origenes`); no lo dupliques en el servicio.
- **Inactividad del VPC Link**: sus interfaces de red se eliminan tras 60 días sin tráfico y se recrean con la siguiente petición, que puede tardar o fallar una vez.

## 8. Permisos que usa tu workflow

Con la política de la plataforma (`apigateway` sobre `/apis/*`, `ssm:GetParameter*` sobre `/urban-alert/*`, `elasticloadbalancing:Describe*`) y la de despliegue que ya existe (`ecs:DescribeServices`, `iam:PutRolePolicy` sobre `role/urban-alert-*`) basta para todo lo anterior.

## 9. Desaprovisionar

Primero cada microservicio (su workflow Deprovision elimina sus rutas, su servicio Express, su ALB y sus grupos de seguridad) y **después** la plataforma. Si queda algo en uso, el Deprovision de la plataforma no elimina nada y lista lo que encontró (grupos de seguridad, ALB, interfaces de red, rutas o integraciones del gateway).

## Lista de comprobación (spec RF-011)

- [ ] Leo las salidas de SSM, sin copiar valores a mano (§1).
- [ ] Despliego en **todas** las subredes privadas (§2).
- [ ] Obtengo el endpoint y el listener después de desplegar el servicio (§3).
- [ ] Cada integración sobrescribe `Host` y verifica el TLS contra el endpoint, con payload 1.0 (§4).
- [ ] Las rutas siguen `/api/v1/<servicio>/`, con reescritura de ruta, solo los métodos públicos y, si hace falta, una cabecera fija (§4).
- [ ] Las rutas internas usan `AWS_IAM` y el servicio que llama firma con SigV4 y tiene `execute-api:Invoke` (§5).
- [ ] Sé qué aislamiento da el ALB interno y cuál no (§6).
- [ ] Mi Deprovision elimina mis rutas antes de que se desaprovisione la plataforma (§9).
