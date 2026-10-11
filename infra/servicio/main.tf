# The three functions of the service and the queue → registro connection (spec RF-001, RF-004, RF-008, RF-011;
# plan D-001, D-002, D-004, §7.3, §7.4). No function URL: the API is only invoked by the shared API Gateway (stack rutas).
data "terraform_remote_state" "base" {
  backend = "s3"

  config = {
    bucket = "urban-alert-tfstate-348364742283"
    key    = "auditoria/base.tfstate"
    region = var.region
  }
}

locals {
  base = data.terraform_remote_state.base.outputs

  # Cloud configuration (plan §7.3): only secret ARNs, never passwords or connection strings (RF-011).
  common_environment = {
    DOTNET_ENVIRONMENT              = "Production"
    ASPNETCORE_ENVIRONMENT          = "Production"
    Logging__Console__FormatterName = "json"
    RabbitMq__Enabled               = "false"
  }

  environment = {
    api = merge(local.common_environment, {
      Autenticacion__Modo                     = "Ninguna"
      Gateway__ExigirClave                    = "false"
      Gateway__PathBase                       = "/api/v1"
      ConnectionStrings__AuditReaderSecretArn = local.base.reader_secret_arn
    })
    registro = merge(local.common_environment, {
      ConnectionStrings__AuditWriterSecretArn = local.base.writer_secret_arn
    })
    init = merge(local.common_environment, {
      ConnectionStrings__AuditWriterSecretArn = local.base.writer_secret_arn
      ConnectionStrings__AuditReaderSecretArn = local.base.reader_secret_arn
      Init__AdminSecretArn                    = local.base.master_secret_arn
    })
  }

  functions = {
    api = {
      description = "UrbanAlert Auditoria: API of the audit history and integrity (shared API Gateway)"
      handler     = "Auditoria.API"
      package     = var.paquete_api
      timeout     = var.api_timeout_s
      reserved    = var.api_concurrencia_reservada
    }
    registro = {
      description = "UrbanAlert Auditoria: records ReporteCreado events from its SQS queue"
      handler     = "Auditoria.Lambda::Auditoria.Lambda.RegistroHandler::ManejarAsync"
      package     = var.paquete_funciones
      timeout     = local.base.registro_timeout_s
      reserved    = -1
    }
    init = {
      description = "UrbanAlert Auditoria: database roles and schema (invoked by the Provision workflow)"
      handler     = "Auditoria.Lambda::Auditoria.Lambda.InitHandler::ManejarAsync"
      package     = var.paquete_funciones
      timeout     = var.init_timeout_s
      reserved    = -1
    }
  }

  # Connection budget (RNF-002): every instance opens at most db_pool_max connections.
  api_max_instances  = var.api_concurrencia_reservada > 0 ? var.api_concurrencia_reservada : var.account_concurrency_limit
  max_db_connections = local.base.db_pool_max * (local.api_max_instances + var.registro_max_concurrency + 1)
}

resource "aws_lambda_function" "this" {
  for_each = local.functions

  function_name = local.base.function_names[each.key]
  description   = each.value.description
  role          = local.base.role_arns[each.key]

  runtime          = "dotnet10"
  architectures    = ["x86_64"]
  handler          = each.value.handler
  filename         = each.value.package
  source_code_hash = filebase64sha256(each.value.package)

  memory_size                    = var.memory_mb
  timeout                        = each.value.timeout
  reserved_concurrent_executions = each.value.reserved

  vpc_config {
    subnet_ids         = local.base.subnet_ids
    security_group_ids = [local.base.lambda_security_group_id]
  }

  environment {
    variables = local.environment[each.key]
  }

  logging_config {
    log_format = "Text"
    log_group  = local.base.log_group_names[each.key]
  }

  tags = {
    Commit = var.commit
  }

  lifecycle {
    precondition {
      condition     = local.max_db_connections <= var.db_connection_budget
      error_message = "The functions could open more database connections than the budget (spec RNF-002)."
    }
  }
}

# Queue → registro: in-order batches, at most registro_max_concurrency invocations (the chain is global, plan D-004),
# and only the failed messages return to the queue (RF-005).
resource "aws_lambda_event_source_mapping" "registro" {
  event_source_arn                   = local.base.queue_arn
  function_name                      = aws_lambda_function.this["registro"].arn
  batch_size                         = var.registro_batch_size
  maximum_batching_window_in_seconds = var.registro_batching_window_s
  function_response_types            = ["ReportBatchItemFailures"]
  enabled                            = true

  scaling_config {
    maximum_concurrency = var.registro_max_concurrency
  }
}
