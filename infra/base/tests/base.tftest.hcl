# terraform test with mocked providers: no AWS credentials, no real resources (constitution P7).
mock_provider "aws" {
  mock_resource "aws_db_instance" {
    defaults = {
      address            = "urban-alert-auditoria-db.example.us-east-2.rds.amazonaws.com"
      port               = 5432
      master_user_secret = [{ secret_arn = "arn:aws:secretsmanager:us-east-2:348364742283:secret:rds!db-mock", kms_key_id = "", secret_status = "active" }]
    }
  }

  mock_resource "aws_iam_role" {
    defaults = { arn = "arn:aws:iam::348364742283:role/urban-alert-auditoria-mock" }
  }

  mock_resource "aws_secretsmanager_secret" {
    defaults = { arn = "arn:aws:secretsmanager:us-east-2:348364742283:secret:urban-alert-auditoria-db-mock" }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = { arn = "arn:aws:logs:us-east-2:348364742283:log-group:/aws/lambda/urban-alert-auditoria-mock" }
  }

  mock_resource "aws_sqs_queue" {
    defaults = {
      arn = "arn:aws:sqs:us-east-2:348364742283:urban-alert-auditoria-eventos-mock"
      id  = "https://sqs.us-east-2.amazonaws.com/348364742283/urban-alert-auditoria-eventos-mock"
    }
  }
}

mock_provider "random" {}

# Platform contract (SSM). StringList values are comma-separated.
override_data {
  target = data.aws_ssm_parameter.vpc_id
  values = { value = "vpc-plataforma" }
}

override_data {
  target = data.aws_ssm_parameter.private_subnets
  values = { value = "subnet-priv-a,subnet-priv-b" }
}

run "defaults_protect_data" {
  command = apply

  assert {
    condition     = aws_db_instance.this.deletion_protection && !aws_db_instance.this.skip_final_snapshot
    error_message = "RDS must have deletion protection and a final snapshot unless allow_destroy is set (RF-009, P4)."
  }

  assert {
    condition     = !aws_db_instance.this.publicly_accessible && aws_db_instance.this.storage_encrypted
    error_message = "RDS must be private and encrypted (RF-009)."
  }

  assert {
    condition     = aws_db_instance.this.backup_retention_period >= 7 && aws_db_instance.this.manage_master_user_password
    error_message = "RDS needs 7 days of backups and an RDS-managed master password (RF-009, RF-011)."
  }

  assert {
    condition     = aws_db_instance.this.engine == "postgres" && aws_db_instance.this.engine_version == "17"
    error_message = "The database is PostgreSQL 17 (plan D-010)."
  }

  assert {
    condition     = aws_db_subnet_group.this.subnet_ids == toset(["subnet-priv-a", "subnet-priv-b"])
    error_message = "The database must use every private subnet of the platform (RF-013)."
  }
}

run "allow_destroy_lifts_protection" {
  command = plan

  variables {
    allow_destroy         = true
    final_snapshot_suffix = "123456"
  }

  assert {
    condition     = !aws_db_instance.this.deletion_protection
    error_message = "allow_destroy must lift the deletion protection (RF-017)."
  }

  assert {
    condition     = aws_db_instance.this.final_snapshot_identifier == "urban-alert-auditoria-final-123456"
    error_message = "The final snapshot must carry the run id."
  }
}

run "network_isolation" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_ingress_rule.rds_from_lambda.referenced_security_group_id == aws_security_group.lambda.id && aws_vpc_security_group_ingress_rule.rds_from_lambda.from_port == 5432 && aws_vpc_security_group_ingress_rule.rds_from_lambda.cidr_ipv4 == null
    error_message = "RDS must only accept PostgreSQL from the functions' security group (RNF-001)."
  }

  assert {
    condition     = aws_vpc_security_group_egress_rule.lambda_rds.referenced_security_group_id == aws_security_group.rds.id
    error_message = "The functions reach PostgreSQL only on the RDS security group."
  }

  assert {
    condition     = aws_security_group.lambda.vpc_id == "vpc-plataforma" && aws_security_group.rds.vpc_id == "vpc-plataforma"
    error_message = "Security groups live in the platform VPC from SSM (RF-013)."
  }
}

run "queues_retain_failures" {
  command = apply

  assert {
    condition     = jsondecode(aws_sqs_queue.eventos.redrive_policy).maxReceiveCount == 5
    error_message = "A message moves to the DLQ after 5 deliveries (RF-005)."
  }

  assert {
    condition     = aws_sqs_queue.dlq.message_retention_seconds >= 1209600
    error_message = "Failed messages are kept at least 14 days (RF-005)."
  }

  assert {
    condition     = aws_sqs_queue.eventos.visibility_timeout_seconds >= 6 * 30
    error_message = "Visibility timeout must be at least 6 x the registro timeout."
  }

  assert {
    condition     = aws_sqs_queue.eventos.sqs_managed_sse_enabled && aws_sqs_queue.dlq.sqs_managed_sse_enabled
    error_message = "Both queues are encrypted."
  }

  assert {
    condition     = aws_sqs_queue.eventos.name == "urban-alert-auditoria-eventos" && aws_sqs_queue.dlq.name == "urban-alert-auditoria-eventos-dlq"
    error_message = "Queue names follow the urban-alert-auditoria prefix (P9; pipeline policy scope)."
  }
}

run "visibility_too_short_fails" {
  command = plan

  variables {
    queue_visibility_timeout_s = 60
  }

  expect_failures = [aws_sqs_queue.eventos]
}

run "least_privilege_roles" {
  command = apply

  assert {
    condition     = toset([for r in aws_iam_role.function : r.name]) == toset(["urban-alert-auditoria-api", "urban-alert-auditoria-registro", "urban-alert-auditoria-init"])
    error_message = "One role per function with the service prefix (D-009)."
  }

  assert {
    condition     = length([for s in jsondecode(aws_iam_role_policy.function["api"].policy).Statement : s if s.Sid == "ReadOwnSecrets" && length(s.Resource) == 1]) == 1
    error_message = "The API reads only one secret (the reader one) (RNF-001)."
  }

  assert {
    condition     = length([for s in jsondecode(aws_iam_role_policy.function["init"].policy).Statement : s if s.Sid == "ReadOwnSecrets" && length(s.Resource) == 3]) == 1
    error_message = "init reads the writer, reader and master secrets."
  }

  assert {
    condition     = length([for s in jsondecode(aws_iam_role_policy.function["registro"].policy).Statement : s if s.Sid == "ConsumeOwnQueue" && s.Resource == aws_sqs_queue.eventos.arn]) == 1
    error_message = "registro consumes only its own queue."
  }

  assert {
    condition     = length([for k in ["api", "init"] : k if length([for s in jsondecode(aws_iam_role_policy.function[k].policy).Statement : s if s.Sid == "ConsumeOwnQueue"]) > 0]) == 0
    error_message = "Only registro may read the queue."
  }

  assert {
    condition     = alltrue([for s in aws_secretsmanager_secret_version.db : strcontains(s.secret_string, "SSL Mode=Require") && strcontains(s.secret_string, "Maximum Pool Size=2")])
    error_message = "Connection strings require TLS and a pool of 2 (RF-009, RNF-002)."
  }
}

run "name_must_keep_prefix" {
  command = plan

  variables {
    name = "otro-servicio"
  }

  expect_failures = [var.name]
}
