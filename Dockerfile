# syntax=docker/dockerfile:1
#
# Imagen de MessageFlow con dos targets:
#   --target api       → la API ASP.NET Core (incluye los workers en segundo plano)
#   --target migrator  → bundle de migraciones EF Core (se ejecuta una vez por deploy)
#
# En CI se construye de forma nativa en un runner ARM64 (ubuntu-24.04-arm) para la
# Orange Pi; localmente también compila en amd64 para validar.

ARG DOTNET_VERSION=10.0

# ── build: restaura y publica la API ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Primero solo los .csproj para aprovechar la caché de capas en el restore.
COPY MessageFlow.slnx dotnet-tools.json ./
COPY src/MessageFlow.Domain/MessageFlow.Domain.csproj                 src/MessageFlow.Domain/
COPY src/MessageFlow.Application/MessageFlow.Application.csproj       src/MessageFlow.Application/
COPY src/MessageFlow.Infrastructure/MessageFlow.Infrastructure.csproj src/MessageFlow.Infrastructure/
COPY src/MessageFlow.Api/MessageFlow.Api.csproj                       src/MessageFlow.Api/
RUN dotnet restore src/MessageFlow.Api/MessageFlow.Api.csproj

COPY src/ src/
RUN dotnet publish src/MessageFlow.Api/MessageFlow.Api.csproj \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ── bundle: ejecutable autocontenido con las migraciones EF Core ──────────────
FROM build AS bundle
ARG TARGETARCH
RUN dotnet tool restore
RUN case "${TARGETARCH:-amd64}" in \
        arm64) RID=linux-arm64 ;; \
        amd64) RID=linux-x64 ;; \
        *) echo "Arquitectura no soportada: ${TARGETARCH}" && exit 1 ;; \
    esac \
 # El restore de la etapa build no incluye el RID; el bundle autocontenido lo necesita.
 && dotnet restore src/MessageFlow.Api/MessageFlow.Api.csproj --runtime "$RID" \
 && dotnet ef migrations bundle \
        --project src/MessageFlow.Infrastructure \
        --startup-project src/MessageFlow.Api \
        --configuration Release \
        --runtime "$RID" --self-contained \
        --output /out/efbundle

# ── migrator: solo el bundle, sin SDK ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS migrator
WORKDIR /migrator
COPY --from=bundle /out/efbundle ./efbundle
USER $APP_UID
# La cadena de conexión se pasa en tiempo de ejecución: --connection "Host=...".
ENTRYPOINT ["./efbundle"]

# ── api: imagen final (target por defecto) ────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS api
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

# Valores no secretos por defecto (SemanticSearch, Scheduling, Logging...). Los
# secretos de este archivo son placeholders y se sobrescriben con variables de entorno.
COPY src/MessageFlow.Api/appsettings.example.json ./appsettings.json

# Carpeta de subidas de Excel (DatasetImportOptions.StorageRootPath = data/uploads).
# Se monta como volumen en producción; aquí se crea con el dueño correcto.
RUN mkdir -p /app/data/uploads && chown -R $APP_UID /app/data

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MessageFlow.Api.dll"]
