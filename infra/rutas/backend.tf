# Bootstrap bucket shared by the UrbanAlert repos (infra/bootstrap/README.md); this service has its own keys.
# Locking with an S3 .tflock object (Terraform >= 1.10).
terraform {
  backend "s3" {
    bucket       = "urban-alert-tfstate-348364742283"
    key          = "auditoria/rutas.tfstate"
    region       = "us-east-2"
    encrypt      = true
    use_lockfile = true
  }
}
