provider "aws" {
  region = var.region

  default_tags {
    tags = {
      Project   = "urban-alert"
      Service   = "auditoria"
      ManagedBy = "terraform"
      Stack     = "base"
    }
  }
}
