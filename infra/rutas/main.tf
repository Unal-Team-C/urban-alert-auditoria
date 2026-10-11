# Routes of the Auditoria service in the shared API Gateway of urban-alert-plataforma (spec RF-006, RF-008; plan D-007).
# The gateway invokes the function directly (AWS_PROXY, payload 2.0, the event of Amazon.Lambda.AspNetCoreServer); the
# platform VPC link is only for the ECS Express load balancers. No path rewrite or fixed header is possible with a
# Lambda integration: the service strips /api/v1 itself (Gateway:PathBase) and only the gateway may invoke it.
# Applied last, so the API is published only when the database is ready (plan §4.3).
data "aws_ssm_parameter" "api_id" {
  name = "/urban-alert/plataforma/api-id"
}

data "aws_ssm_parameter" "api_execution_arn" {
  name = "/urban-alert/plataforma/api-execution-arn"
}

data "aws_ssm_parameter" "api_endpoint" {
  name = "/urban-alert/plataforma/api-endpoint"
}

data "terraform_remote_state" "servicio" {
  backend = "s3"

  config = {
    bucket = "urban-alert-tfstate-348364742283"
    key    = "auditoria/servicio.tfstate"
    region = var.region
  }
}

locals {
  # The provider marks SSM values as sensitive; these IDs are not secrets.
  api_id            = nonsensitive(data.aws_ssm_parameter.api_id.value)
  api_execution_arn = nonsensitive(data.aws_ssm_parameter.api_execution_arn.value)
  servicio          = data.terraform_remote_state.servicio.outputs

  # Public, as every route of the platform (no client authentication in the gateway, spec RF-007). Nothing else is
  # published: /health, /scalar, /openapi and any other path keep answering 404 from the gateway.
  routes = {
    historial  = "GET /api/v1/auditoria/reportes/{reportId}"
    integridad = "GET /api/v1/auditoria/reportes/{reportId}/integridad"
  }
}

resource "aws_apigatewayv2_integration" "api" {
  api_id                 = local.api_id
  description            = "Lambda ${local.servicio.api_function_name}"
  integration_type       = "AWS_PROXY"
  integration_method     = "POST"
  integration_uri        = local.servicio.api_invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_route" "this" {
  for_each = local.routes

  api_id             = local.api_id
  route_key          = each.value
  authorization_type = "NONE"
  target             = "integrations/${aws_apigatewayv2_integration.api.id}"
}

# Only the shared gateway, and only for GET under the Auditoria reports prefix, may invoke the function (RF-008).
resource "aws_lambda_permission" "gateway" {
  statement_id  = "AllowSharedGatewayAuditoriaRoutes"
  action        = "lambda:InvokeFunction"
  function_name = local.servicio.api_function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${local.api_execution_arn}/*/GET/api/v1/auditoria/reportes/*"
}
