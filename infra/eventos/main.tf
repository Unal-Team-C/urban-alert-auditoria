# Auditoria rule on the shared bus and its only target, the service queue (spec RF-001, RF-002; plan D-005).
# Applied after the init function, so events only flow once registro can write; destroyed before the functions.
# The bus, the Notificaciones rule and its queue are not managed here (RF-014).
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
}

resource "aws_cloudwatch_event_rule" "reporte_creado" {
  name           = var.rule_name
  description    = "ReporteCreado events of urban-alert-reportes for the Auditoria service"
  event_bus_name = var.event_bus_arn
  state          = "ENABLED"

  # Same event Reportes publishes (its README, "Evento en EventBridge").
  event_pattern = jsonencode({
    source        = ["urban-alert.reportes"]
    "detail-type" = ["ReporteCreado"]
  })
}

# Whole EventBridge envelope as the message body (no input transformer): registro validates source and detail-type.
resource "aws_cloudwatch_event_target" "cola" {
  rule           = aws_cloudwatch_event_rule.reporte_creado.name
  event_bus_name = var.event_bus_arn
  target_id      = "cola-auditoria"
  arn            = local.base.queue_arn
}

# Only this rule may send messages to the queue (RNF-001).
resource "aws_sqs_queue_policy" "eventos" {
  queue_url = local.base.queue_url

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "AuditoriaRuleOnly"
      Effect    = "Allow"
      Principal = { Service = "events.amazonaws.com" }
      Action    = "sqs:SendMessage"
      Resource  = local.base.queue_arn
      Condition = {
        ArnEquals = { "aws:SourceArn" = aws_cloudwatch_event_rule.reporte_creado.arn }
      }
    }]
  })
}
