# terraform test with mocked providers: no AWS credentials, no real resources (constitution P7).
mock_provider "aws" {
  mock_resource "aws_cloudwatch_event_rule" {
    defaults = { arn = "arn:aws:events:us-east-2:348364742283:rule/urban-alert-bus-eventos/urban-alert-auditoria-reporte-creado" }
  }
}

override_data {
  target = data.terraform_remote_state.base
  values = {
    outputs = {
      queue_arn = "arn:aws:sqs:us-east-2:348364742283:urban-alert-auditoria-eventos"
      queue_url = "https://sqs.us-east-2.amazonaws.com/348364742283/urban-alert-auditoria-eventos"
    }
  }
}

run "rule_selects_reporte_creado_on_the_shared_bus" {
  command = apply

  assert {
    condition     = jsondecode(aws_cloudwatch_event_rule.reporte_creado.event_pattern) == { source = ["urban-alert.reportes"], "detail-type" = ["ReporteCreado"] }
    error_message = "The rule pattern is exactly the ReporteCreado event of Reportes (RF-002)."
  }

  assert {
    condition     = aws_cloudwatch_event_rule.reporte_creado.event_bus_name == "arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos" && aws_cloudwatch_event_rule.reporte_creado.name == "urban-alert-auditoria-reporte-creado"
    error_message = "The rule lives on the shared bus with the Auditoria name (RF-002)."
  }

  assert {
    condition     = aws_cloudwatch_event_target.cola.arn == "arn:aws:sqs:us-east-2:348364742283:urban-alert-auditoria-eventos" && length(coalesce(aws_cloudwatch_event_target.cola.input_transformer, [])) == 0 && aws_cloudwatch_event_target.cola.input == null && aws_cloudwatch_event_target.cola.input_path == null
    error_message = "The only target is the queue and it receives the whole envelope (RF-001)."
  }
}

run "queue_accepts_only_the_rule" {
  command = apply

  assert {
    condition     = jsondecode(aws_sqs_queue_policy.eventos.policy).Statement[0].Condition.ArnEquals["aws:SourceArn"] == aws_cloudwatch_event_rule.reporte_creado.arn
    error_message = "Only the Auditoria rule may send to the queue (RNF-001)."
  }

  assert {
    condition     = length(jsondecode(aws_sqs_queue_policy.eventos.policy).Statement) == 1 && jsondecode(aws_sqs_queue_policy.eventos.policy).Statement[0].Action == "sqs:SendMessage"
    error_message = "The queue policy grants nothing else."
  }
}

run "rule_name_keeps_the_prefix" {
  command = plan

  variables {
    rule_name = "urban-alert-notificaciones"
  }

  expect_failures = [var.rule_name]
}
