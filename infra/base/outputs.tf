# Read by the servicio and eventos stacks (terraform_remote_state) and by the workflows. No password or connection
# string is an output (constitution P3): the functions only get secret ARNs.
output "subnet_ids" {
  value = local.private_subnets
}

output "lambda_security_group_id" {
  value = aws_security_group.lambda.id
}

output "function_names" {
  value = local.functions
}

output "role_arns" {
  value = { for key, role in aws_iam_role.function : key => role.arn }
}

output "log_group_names" {
  value = { for key, group in aws_cloudwatch_log_group.function : key => group.name }
}

output "writer_secret_arn" {
  value = aws_secretsmanager_secret.db["writer"].arn
}

output "reader_secret_arn" {
  value = aws_secretsmanager_secret.db["reader"].arn
}

output "master_secret_arn" {
  value = local.master_secret_arn
}

output "queue_arn" {
  value = aws_sqs_queue.eventos.arn
}

output "queue_url" {
  value = aws_sqs_queue.eventos.id
}

output "dlq_arn" {
  value = aws_sqs_queue.dlq.arn
}

output "dlq_url" {
  value = aws_sqs_queue.dlq.id
}

output "registro_timeout_s" {
  value = var.registro_timeout_s
}

output "db_pool_max" {
  value = var.db_pool_max
}

output "db_instance_identifier" {
  value = aws_db_instance.this.identifier
}
