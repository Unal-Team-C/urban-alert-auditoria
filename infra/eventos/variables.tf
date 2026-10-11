variable "region" {
  description = "AWS region."
  type        = string
}

variable "event_bus_arn" {
  description = "Shared EventBridge bus that replaces RabbitMQ (created by hand, not managed by any repo). Only the Auditoria rule is created on it."
  type        = string

  validation {
    condition     = can(regex("^arn:aws:events:[a-z0-9-]+:[0-9]{12}:event-bus/[A-Za-z0-9._-]+$", var.event_bus_arn))
    error_message = "event_bus_arn must be the ARN of an EventBridge bus."
  }
}

variable "rule_name" {
  description = "Name of the Auditoria rule. The pipeline policy only allows rules named urban-alert-auditoria-*."
  type        = string

  validation {
    condition     = startswith(var.rule_name, "urban-alert-auditoria-")
    error_message = "rule_name must start with \"urban-alert-auditoria-\" (infra/bootstrap/github-auditoria-policy.json)."
  }
}
