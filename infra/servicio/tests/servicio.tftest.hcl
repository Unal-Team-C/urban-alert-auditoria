# terraform test with mocked providers: no AWS credentials, no real resources (constitution P7).
mock_provider "aws" {
  mock_resource "aws_lambda_function" {
    defaults = {
      arn        = "arn:aws:lambda:us-east-2:348364742283:function:urban-alert-auditoria-mock"
      invoke_arn = "arn:aws:apigateway:us-east-2:lambda:path/2015-03-31/functions/arn:aws:lambda:us-east-2:348364742283:function:urban-alert-auditoria-api/invocations"
    }
  }
}

override_data {
  target = data.terraform_remote_state.base
  values = {
    outputs = {
      subnet_ids               = ["subnet-priv-a", "subnet-priv-b"]
      lambda_security_group_id = "sg-lambda"
      function_names = {
        api      = "urban-alert-auditoria-api"
        registro = "urban-alert-auditoria-registro"
        init     = "urban-alert-auditoria-init"
      }
      role_arns = {
        api      = "arn:aws:iam::348364742283:role/urban-alert-auditoria-api"
        registro = "arn:aws:iam::348364742283:role/urban-alert-auditoria-registro"
        init     = "arn:aws:iam::348364742283:role/urban-alert-auditoria-init"
      }
      log_group_names = {
        api      = "/aws/lambda/urban-alert-auditoria-api"
        registro = "/aws/lambda/urban-alert-auditoria-registro"
        init     = "/aws/lambda/urban-alert-auditoria-init"
      }
      writer_secret_arn  = "arn:aws:secretsmanager:us-east-2:348364742283:secret:urban-alert-auditoria-db-writer-AbCdEf"
      reader_secret_arn  = "arn:aws:secretsmanager:us-east-2:348364742283:secret:urban-alert-auditoria-db-reader-AbCdEf"
      master_secret_arn  = "arn:aws:secretsmanager:us-east-2:348364742283:secret:rds!db-mock"
      queue_arn          = "arn:aws:sqs:us-east-2:348364742283:urban-alert-auditoria-eventos"
      registro_timeout_s = 30
      db_pool_max        = 2
    }
  }
}

variables {
  paquete_api       = "tests/fixture.zip"
  paquete_funciones = "tests/fixture.zip"
  commit            = "0123456789abcdef"
}

run "functions_use_the_managed_dotnet_runtime_in_the_vpc" {
  command = apply

  assert {
    condition     = alltrue([for f in aws_lambda_function.this : f.runtime == "dotnet10" && f.architectures == tolist(["x86_64"])])
    error_message = "Every function runs on the managed dotnet10 runtime, x86_64 (plan D-001)."
  }

  assert {
    condition     = alltrue([for f in aws_lambda_function.this : toset(f.vpc_config[0].subnet_ids) == toset(["subnet-priv-a", "subnet-priv-b"]) && f.vpc_config[0].security_group_ids == toset(["sg-lambda"])])
    error_message = "Every function runs in all private subnets with the functions' security group (RF-009)."
  }

  assert {
    condition     = aws_lambda_function.this["api"].handler == "Auditoria.API" && aws_lambda_function.this["registro"].handler == "Auditoria.Lambda::Auditoria.Lambda.RegistroHandler::ManejarAsync" && aws_lambda_function.this["init"].handler == "Auditoria.Lambda::Auditoria.Lambda.InitHandler::ManejarAsync"
    error_message = "Handlers of plan §7.4."
  }

  assert {
    condition     = aws_lambda_function.this["api"].timeout <= 29 && aws_lambda_function.this["registro"].timeout == 30 && aws_lambda_function.this["api"].reserved_concurrent_executions == -1
    error_message = "Timeouts and (no) reserved concurrency of plan §7.4 and D-010."
  }

  assert {
    condition     = alltrue([for f in aws_lambda_function.this : f.tags["Commit"] == "0123456789abcdef"])
    error_message = "Every function is tagged with the deployed commit."
  }
}

run "environment_has_only_secret_arns" {
  command = apply

  assert {
    condition = alltrue([for f in aws_lambda_function.this : alltrue([
      for k, v in f.environment[0].variables : !strcontains(v, "Password=") && !strcontains(lower(k), "password") && (startswith(k, "ConnectionStrings__") ? endswith(k, "SecretArn") : true)
    ])])
    error_message = "No password or connection string in the environment, only secret ARNs (RF-011)."
  }

  assert {
    condition     = aws_lambda_function.this["api"].environment[0].variables["Autenticacion__Modo"] == "Ninguna" && aws_lambda_function.this["api"].environment[0].variables["Gateway__ExigirClave"] == "false" && aws_lambda_function.this["api"].environment[0].variables["Gateway__PathBase"] == "/api/v1"
    error_message = "Cloud configuration of the API (plan §7.3, D-006, D-007)."
  }

  assert {
    condition     = !contains(keys(aws_lambda_function.this["api"].environment[0].variables), "ConnectionStrings__AuditWriterSecretArn") && !contains(keys(aws_lambda_function.this["api"].environment[0].variables), "Init__AdminSecretArn")
    error_message = "The API only gets the reader secret (RNF-001)."
  }

  assert {
    condition     = !contains(keys(aws_lambda_function.this["registro"].environment[0].variables), "ConnectionStrings__AuditReaderSecretArn") && aws_lambda_function.this["init"].environment[0].variables["Init__AdminSecretArn"] == "arn:aws:secretsmanager:us-east-2:348364742283:secret:rds!db-mock"
    error_message = "registro gets the writer secret; only init gets the master secret."
  }

  assert {
    condition     = alltrue([for f in aws_lambda_function.this : f.environment[0].variables["RabbitMq__Enabled"] == "false" && f.environment[0].variables["Logging__Console__FormatterName"] == "json"])
    error_message = "No RabbitMQ in the cloud and JSON logs (RF-012)."
  }
}

run "queue_feeds_registro_with_bounded_concurrency" {
  command = apply

  assert {
    condition     = aws_lambda_event_source_mapping.registro.event_source_arn == "arn:aws:sqs:us-east-2:348364742283:urban-alert-auditoria-eventos" && aws_lambda_event_source_mapping.registro.enabled
    error_message = "The registro function reads its own queue (RF-001)."
  }

  assert {
    condition     = aws_lambda_event_source_mapping.registro.scaling_config[0].maximum_concurrency == 2
    error_message = "At most 2 concurrent invocations (plan D-004, RNF-002)."
  }

  assert {
    condition     = aws_lambda_event_source_mapping.registro.function_response_types == toset(["ReportBatchItemFailures"])
    error_message = "Partial batch failures (RF-005)."
  }

  assert {
    condition     = output.max_db_connections <= 40
    error_message = "Connection budget (RNF-002)."
  }
}

run "connection_budget_is_enforced" {
  command = plan

  variables {
    account_concurrency_limit = 30
  }

  expect_failures = [aws_lambda_function.this]
}

run "api_timeout_below_gateway_limit" {
  command = plan

  variables {
    api_timeout_s = 30
  }

  expect_failures = [var.api_timeout_s]
}
