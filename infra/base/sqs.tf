# Buffer between the Auditoria rule of the shared bus (stack eventos) and the registro function (spec RF-001, RF-005;
# plan D-004). The access policy that lets only that rule send messages lives in the eventos stack, next to the rule.
resource "aws_sqs_queue" "dlq" {
  name                      = "${var.name}-eventos-dlq"
  message_retention_seconds = var.dlq_retention_s
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "eventos" {
  name                       = "${var.name}-eventos"
  message_retention_seconds  = var.queue_retention_s
  visibility_timeout_seconds = var.queue_visibility_timeout_s
  sqs_managed_sse_enabled    = true

  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dlq.arn
    maxReceiveCount     = var.queue_max_receive_count
  })

  lifecycle {
    # AWS guidance for Lambda event sources: visibility timeout >= 6 x function timeout.
    precondition {
      condition     = var.queue_visibility_timeout_s >= 6 * var.registro_timeout_s
      error_message = "queue_visibility_timeout_s must be at least 6 times registro_timeout_s."
    }
  }
}

# Only the main queue may use the DLQ as its dead-letter queue.
resource "aws_sqs_queue_redrive_allow_policy" "dlq" {
  queue_url = aws_sqs_queue.dlq.id

  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.eventos.arn]
  })
}
