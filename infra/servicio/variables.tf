variable "region" {
  description = "AWS region."
  type        = string
}

variable "paquete_api" {
  description = "Path of build/api.zip (scripts/build-lambda-zips.sh); the deprovision workflow passes an empty file."
  type        = string
}

variable "paquete_funciones" {
  description = "Path of build/funciones.zip (scripts/build-lambda-zips.sh); the deprovision workflow passes an empty file."
  type        = string
}

variable "commit" {
  description = "Commit SHA of the deployed packages (tag Commit)."
  type        = string
}

variable "memory_mb" {
  type = number
}

variable "api_timeout_s" {
  description = "Timeout of the API function."
  type        = number

  validation {
    condition     = var.api_timeout_s <= 29
    error_message = "api_timeout_s must stay below the 30 s integration timeout of the API Gateway."
  }
}

variable "init_timeout_s" {
  type = number
}

variable "api_concurrencia_reservada" {
  description = "Reserved concurrency of the API function; -1 = none (plan D-010: the account has 10 in total and Lambda keeps 10 unreserved). Its real cap is the account limit."
  type        = number
}

variable "registro_max_concurrency" {
  description = "Maximum concurrent invocations of registro from the queue (plan D-004; the SQS minimum is 2)."
  type        = number

  validation {
    condition     = var.registro_max_concurrency >= 2 && var.registro_max_concurrency <= 1000
    error_message = "registro_max_concurrency must be between 2 and 1000 (SQS event source limits)."
  }
}

variable "registro_batch_size" {
  type = number
}

variable "registro_batching_window_s" {
  type = number
}

variable "account_concurrency_limit" {
  description = "Concurrent executions of the account (infra/bootstrap/README.md); upper bound of the API instances when it reserves none."
  type        = number
}

variable "db_connection_budget" {
  description = "Maximum connections of the functions to the database (spec RNF-002)."
  type        = number
  default     = 40
}
