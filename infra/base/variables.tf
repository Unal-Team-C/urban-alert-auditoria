variable "region" {
  description = "AWS region."
  type        = string
}

variable "name" {
  description = "Prefix for every resource of this service. The pipeline's IAM permissions are scoped to urban-alert-*."
  type        = string

  validation {
    condition     = startswith(var.name, "urban-alert-auditoria")
    error_message = "name must start with \"urban-alert-auditoria\" (constitution P9)."
  }
}

variable "allow_destroy" {
  description = "Only the deprovision workflow sets it: disables the RDS deletion protection."
  type        = bool
  default     = false
}

variable "final_snapshot_suffix" {
  description = "Suffix of the final RDS snapshot taken on destroy (the deprovision workflow passes its run id)."
  type        = string
  default     = "manual"
}

variable "db_name" {
  description = "Database created in the RDS instance."
  type        = string
}

variable "db_password_version" {
  description = "Rotation counter of the audit_writer and audit_reader passwords; change it to generate new ones (the next init applies them)."
  type        = string
}

variable "db_pool_max" {
  description = "Maximum connections per function instance (Npgsql pool). infra/servicio checks the total against the budget."
  type        = number
  default     = 2
}

variable "db_engine_version" {
  description = "PostgreSQL major version."
  type        = string
}

variable "db_instance_class" {
  type = string
}

variable "db_allocated_storage_gb" {
  type = number
}

variable "db_max_allocated_storage_gb" {
  type = number
}

variable "db_backup_retention_days" {
  type = number

  validation {
    condition     = var.db_backup_retention_days >= 7
    error_message = "Backups must be kept at least 7 days (spec RF-009)."
  }
}

variable "registro_timeout_s" {
  description = "Timeout of the registro function; the queue visibility timeout must be at least 6 times this value."
  type        = number
  default     = 30
}

variable "queue_visibility_timeout_s" {
  type    = number
  default = 180

  validation {
    condition     = var.queue_visibility_timeout_s >= 30 && var.queue_visibility_timeout_s <= 43200
    error_message = "queue_visibility_timeout_s must be between 30 and 43200 seconds."
  }
}

variable "queue_max_receive_count" {
  description = "Deliveries of a message before it moves to the dead-letter queue (spec RF-005)."
  type        = number
  default     = 5
}

variable "queue_retention_s" {
  type    = number
  default = 345600 # 4 days
}

variable "dlq_retention_s" {
  type    = number
  default = 1209600 # 14 days, the SQS maximum (spec RF-005)

  validation {
    condition     = var.dlq_retention_s >= 1209600
    error_message = "Failed messages must be kept at least 14 days (spec RF-005)."
  }
}

variable "log_retention_days" {
  type = number
}

variable "pausada" {
  description = "Only the Pausar workflow sets it to true: stops the RDS instance (data and storage are kept). Provision leaves it false; it starts a stopped instance first with scripts/arrancar-rds.sh, because this resource cannot start one."
  type        = bool
  default     = false
}
