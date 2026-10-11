output "rule_arn" {
  value = aws_cloudwatch_event_rule.reporte_creado.arn
}

output "rule_name" {
  value = aws_cloudwatch_event_rule.reporte_creado.name
}
