# Roles of the three functions, with inline policies only (the pipeline may not attach managed policies to
# urban-alert-* roles, as in Multimedia). Least privilege (spec RNF-001, plan D-009): the API only reads the reader
# secret; registro the writer secret and its queue; init both secrets and the RDS master secret.
locals {
  functions = {
    api      = "${var.name}-api"
    registro = "${var.name}-registro"
    init     = "${var.name}-init"
  }

  master_secret_arn = aws_db_instance.this.master_user_secret[0].secret_arn

  secrets_by_function = {
    api      = [aws_secretsmanager_secret.db["reader"].arn]
    registro = [aws_secretsmanager_secret.db["writer"].arn]
    init     = [aws_secretsmanager_secret.db["writer"].arn, aws_secretsmanager_secret.db["reader"].arn, local.master_secret_arn]
  }

  # Same permissions as AWSLambdaVPCAccessExecutionRole: ENIs in the private subnets.
  vpc_access = {
    Sid    = "VpcNetworkInterfaces"
    Effect = "Allow"
    Action = [
      "ec2:CreateNetworkInterface",
      "ec2:DescribeNetworkInterfaces",
      "ec2:DescribeSubnets",
      "ec2:DeleteNetworkInterface",
      "ec2:AssignPrivateIpAddresses",
      "ec2:UnassignPrivateIpAddresses",
    ]
    Resource = "*"
  }
}

resource "aws_iam_role" "function" {
  for_each = local.functions

  name        = each.value
  description = "Lambda function ${each.value}"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "function" {
  for_each = local.functions

  name = "${each.value}-runtime"
  role = aws_iam_role.function[each.key].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = concat(
      [
        local.vpc_access,
        {
          Sid      = "ReadOwnSecrets"
          Effect   = "Allow"
          Action   = "secretsmanager:GetSecretValue"
          Resource = local.secrets_by_function[each.key]
        },
        {
          Sid      = "OwnLogGroup"
          Effect   = "Allow"
          Action   = ["logs:CreateLogStream", "logs:PutLogEvents"]
          Resource = "${aws_cloudwatch_log_group.function[each.key].arn}:*"
        },
      ],
      each.key == "registro" ? [{
        Sid      = "ConsumeOwnQueue"
        Effect   = "Allow"
        Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes", "sqs:ChangeMessageVisibility"]
        Resource = aws_sqs_queue.eventos.arn
      }] : [],
    )
  })
}
