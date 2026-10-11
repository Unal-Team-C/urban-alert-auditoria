# Read by the stack rutas (terraform_remote_state) and by the workflows (init invocation, smoke test, summary).
output "api_function_name" {
  value = aws_lambda_function.this["api"].function_name
}

output "api_invoke_arn" {
  value = aws_lambda_function.this["api"].invoke_arn
}

output "registro_function_name" {
  value = aws_lambda_function.this["registro"].function_name
}

output "init_function_name" {
  value = aws_lambda_function.this["init"].function_name
}

output "event_source_mapping_uuid" {
  value = aws_lambda_event_source_mapping.registro.uuid
}

output "max_db_connections" {
  value = local.max_db_connections
}
