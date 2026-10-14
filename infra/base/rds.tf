# The service's own PostgreSQL instance (spec RF-009, plan D-010): private, encrypted, TLS enforced by the default
# rds.force_ssl=1 of PostgreSQL >= 15. The master password is managed by RDS in Secrets Manager; only the init function
# reads it. Deletion protection is only lifted by the deprovision workflow (allow_destroy).
resource "aws_db_instance" "this" {
  identifier     = "${var.name}-db"
  engine         = "postgres"
  engine_version = var.db_engine_version
  instance_class = var.db_instance_class
  db_name        = var.db_name

  username                    = "postgres"
  manage_master_user_password = true

  allocated_storage     = var.db_allocated_storage_gb
  max_allocated_storage = var.db_max_allocated_storage_gb
  storage_type          = "gp3"
  storage_encrypted     = true

  multi_az               = false
  publicly_accessible    = false
  db_subnet_group_name   = aws_db_subnet_group.this.name
  vpc_security_group_ids = [aws_security_group.rds.id]

  backup_retention_period    = var.db_backup_retention_days
  auto_minor_version_upgrade = true
  copy_tags_to_snapshot      = true

  deletion_protection       = !var.allow_destroy
  skip_final_snapshot       = false
  final_snapshot_identifier = "${var.name}-final-${var.final_snapshot_suffix}"
}

# Pause without deprovisioning (Pausar / Reanudar workflows of urban-alert-plataforma). AWS restarts a stopped
# instance after 7 days; the daily Mantener pausa workflow of the platform stops it again.
resource "aws_rds_instance_state" "this" {
  identifier = aws_db_instance.this.identifier
  state      = var.pausada ? "stopped" : "available"
}
