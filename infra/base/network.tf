# Shared private network of the UrbanAlert platform (repo urban-alert-plataforma, contract in SSM; spec RF-013).
# Fails with ParameterNotFound when the platform is not provisioned (the workflow checks it first, RF-016).
# The functions have no public IP and leave through the platform NAT; RDS is only reachable from them (RF-009).
data "aws_ssm_parameter" "vpc_id" {
  name = "/urban-alert/plataforma/vpc-id"
}

data "aws_ssm_parameter" "private_subnets" {
  name = "/urban-alert/plataforma/subredes-privadas"
}

# The provider marks every SSM value as sensitive; network IDs are not secrets and are needed as outputs.
locals {
  vpc_id          = nonsensitive(data.aws_ssm_parameter.vpc_id.value)
  private_subnets = split(",", nonsensitive(data.aws_ssm_parameter.private_subnets.value))
}

resource "aws_security_group" "lambda" {
  name        = "${var.name}-lambda"
  description = "Lambda functions of ${var.name} (api, registro, init)"
  vpc_id      = local.vpc_id
}

# Secrets Manager and CloudWatch through the platform NAT.
resource "aws_vpc_security_group_egress_rule" "lambda_https" {
  security_group_id = aws_security_group.lambda.id
  description       = "HTTPS to AWS APIs (Secrets Manager) through the NAT"
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "lambda_rds" {
  security_group_id            = aws_security_group.lambda.id
  description                  = "PostgreSQL to the ${var.name} database"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.rds.id
}

resource "aws_security_group" "rds" {
  name        = "${var.name}-rds"
  description = "RDS of ${var.name}: PostgreSQL only from its Lambda functions"
  vpc_id      = local.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "rds_from_lambda" {
  security_group_id            = aws_security_group.rds.id
  description                  = "PostgreSQL from ${var.name} functions"
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
  referenced_security_group_id = aws_security_group.lambda.id
}

resource "aws_db_subnet_group" "this" {
  name       = "${var.name}-db"
  subnet_ids = local.private_subnets
}
