# Values are documented in variables.tf; change them here, in a commit, never by hand in AWS.
# paquete_api, paquete_funciones and commit are passed by the workflows.
region                     = "us-east-2"
memory_mb                  = 512
api_timeout_s              = 15
init_timeout_s             = 60
api_concurrencia_reservada = -1
registro_max_concurrency   = 2
registro_batch_size        = 10
registro_batching_window_s = 5
account_concurrency_limit  = 10
