# Passwords of the application roles (created or synchronised by the init function, plan §6). They live in the
# encrypted, private Terraform state. Alphanumeric only: they go inside a Npgsql connection string without quoting.
# Change db_password_version to rotate them; the next Provision applies them through the init function.
resource "random_password" "db" {
  for_each = toset(["writer", "reader"])

  length  = 32
  special = false

  keepers = {
    version = var.db_password_version
  }
}

locals {
  db_roles = {
    writer = "audit_writer"
    reader = "audit_reader"
  }
}

# One secret per role so each function can only read its own (plan D-009). recovery_window_in_days = 0: deleted
# immediately on deprovision, so the next provision can recreate the name.
resource "aws_secretsmanager_secret" "db" {
  for_each = local.db_roles

  name                    = "${var.name}-db-${each.key}"
  description             = "Connection of ${var.name} to its database as ${each.value} (TLS required)"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "db" {
  for_each = local.db_roles

  secret_id = aws_secretsmanager_secret.db[each.key].id
  secret_string = jsonencode({
    username = each.value
    password = random_password.db[each.key].result
    host     = aws_db_instance.this.address
    port     = aws_db_instance.this.port
    dbname   = var.db_name
    # TLS required; small pool per function instance (plan §7.3, RNF-002); no GSS/Kerberos probe on Lambda.
    connection_string = join(";", [
      "Host=${aws_db_instance.this.address}",
      "Port=${aws_db_instance.this.port}",
      "Database=${var.db_name}",
      "Username=${each.value}",
      "Password=${random_password.db[each.key].result}",
      "SSL Mode=Require",
      "GSS Encryption Mode=Disable",
      "Maximum Pool Size=${var.db_pool_max}",
      "Minimum Pool Size=0",
    ])
  })
}
