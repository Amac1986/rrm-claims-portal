# syntax=docker/dockerfile:1
#
# ClaimPortal.Api - the HTTP edge. Build context is the repo root (central package
# management), so build with:
#   docker build -f docker/api/Dockerfile .

ARG DOTNET_VERSION=10.0

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/ClaimPortal.Contracts/ClaimPortal.Contracts.csproj src/ClaimPortal.Contracts/
COPY src/ClaimPortal.Domain/ClaimPortal.Domain.csproj src/ClaimPortal.Domain/
COPY src/ClaimPortal.Infrastructure/ClaimPortal.Infrastructure.csproj src/ClaimPortal.Infrastructure/
COPY src/ClaimPortal.Api/ClaimPortal.Api.csproj src/ClaimPortal.Api/

RUN dotnet restore src/ClaimPortal.Api/ClaimPortal.Api.csproj

COPY src/ src/

RUN dotnet publish src/ClaimPortal.Api/ClaimPortal.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
    && rm -f /app/publish/*.pdb

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final

ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_URLS=http://+:8080

WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "ClaimPortal.Api.dll"]
