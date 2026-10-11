# Contexto de build: el directorio que contiene, como hermanos, este repo
# (urban-alert-auditoria) y UrbanAlert/backend/UrbanAlert.Reportes (Application
# aporta el contrato de eventos ReporteCreadoEvent). Ejemplo, desde sources/:
#   docker build -f urban-alert-auditoria/deploy/docker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Primero solo los .csproj, para cachear el restore mientras no cambien las dependencias.
COPY UrbanAlert/backend/UrbanAlert.Reportes/src/Domain/Domain.csproj UrbanAlert/backend/UrbanAlert.Reportes/src/Domain/
COPY UrbanAlert/backend/UrbanAlert.Reportes/src/Application/Application.csproj UrbanAlert/backend/UrbanAlert.Reportes/src/Application/
COPY urban-alert-auditoria/src/Domain/Auditoria.Domain.csproj urban-alert-auditoria/src/Domain/
COPY urban-alert-auditoria/src/Application/Auditoria.Application.csproj urban-alert-auditoria/src/Application/
COPY urban-alert-auditoria/src/Infrastructure/Auditoria.Infrastructure.csproj urban-alert-auditoria/src/Infrastructure/
COPY urban-alert-auditoria/src/API/Auditoria.API.csproj urban-alert-auditoria/src/API/
RUN dotnet restore urban-alert-auditoria/src/API/Auditoria.API.csproj

COPY UrbanAlert/backend/UrbanAlert.Reportes/src/Domain/ UrbanAlert/backend/UrbanAlert.Reportes/src/Domain/
COPY UrbanAlert/backend/UrbanAlert.Reportes/src/Application/ UrbanAlert/backend/UrbanAlert.Reportes/src/Application/
COPY urban-alert-auditoria/src/ urban-alert-auditoria/src/
RUN dotnet publish urban-alert-auditoria/src/API/Auditoria.API.csproj -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Auditoria.API.dll"]
