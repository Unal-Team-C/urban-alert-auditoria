# Investigación: 001-migracion-aws

## Runtime de Lambda para .NET 10 (2026-10-11)

- AWS Lambda tiene runtime gestionado `dotnet10` desde el 2026-01-08 (anuncio «AWS Lambda adds support for .NET 10», https://aws.amazon.com/about-aws/whats-new/2026/01/aws-lambda-dot-net-10/; blog https://aws.amazon.com/blogs/compute/net-10-runtime-now-available-in-aws-lambda). Disponible en todas las regiones; soporte hasta ~noviembre de 2028.
- Conclusión: runtime gestionado y .zip framework-dependent (D-001); no hace falta imagen ni `provided.al2023`.

## Paquetes NuGet (últimas estables vistas en nuget.org el 2026-10-11)

| Paquete | Versión |
|---|---|
| Amazon.Lambda.AspNetCoreServer.Hosting | 2.2.1 |
| Amazon.Lambda.Core | 3.4.0 |
| Amazon.Lambda.SQSEvents | 3.0.1 |
| Amazon.Lambda.Serialization.SystemTextJson | 3.0.1 |
| Amazon.Lambda.RuntimeSupport | 2.3.0 |
| AWSSDK.SecretsManager | 4.0.100.16 |
| Amazon.Lambda.TestUtilities | 4.3.0 |

Al implementar se fija la versión exacta con ≥ 2 semanas de antigüedad.

## Integraciones Lambda en HTTP API

- `AWS_PROXY` no admite `overwrite:path` ni cabeceras fijas (parameter mapping solo para integraciones HTTP / servicios AWS) → D-007.
- `maximum_concurrency` del mapeo SQS → Lambda: mínimo 2, máximo 1000 → D-004.
