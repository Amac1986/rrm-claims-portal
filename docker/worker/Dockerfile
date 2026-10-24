# syntax=docker/dockerfile:1
#
# ClaimPortal.Notifications — the MassTransit consumer that renders and sends the
# confirmation email. Build context is the repo root (central package management needs
# Directory.Packages.props), so build with:
#   docker build -f docker/worker/Dockerfile .
#
# Base image note: the runtime image is the Ubuntu-based mcr.microsoft.com/dotnet/runtime,
# NOT an -alpine or -noble-chiseled variant. ClaimPortal.Notifications.csproj deliberately
# sets InvariantGlobalization=false because the email formats the incident date in en-CA and
# converts the submission timestamp into America/Winnipeg. That needs ICU and tzdata.
# Verified present in this base image (libicuuc.so.74 and /usr/share/zoneinfo/America/Winnipeg),
# so no apt-get layer is required — adding one would be dead weight.
# Rejected: runtime-alpine + `apk add icu-libs tzdata`. Smaller, but it trades a verified
# stock image for a hand-assembled one to save a few MB on a worker that runs one replica.

ARG DOTNET_VERSION=10.0

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore before copying source so a code-only edit reuses the cached package layer.
# Only the files restore actually reads are copied here.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ClaimPortal.Contracts/ClaimPortal.Contracts.csproj src/ClaimPortal.Contracts/
COPY src/ClaimPortal.Notifications/ClaimPortal.Notifications.csproj src/ClaimPortal.Notifications/

RUN dotnet restore src/ClaimPortal.Notifications/ClaimPortal.Notifications.csproj

COPY src/ClaimPortal.Contracts/ src/ClaimPortal.Contracts/
COPY src/ClaimPortal.Notifications/ src/ClaimPortal.Notifications/

# No -r/--self-contained: the runtime image already carries the framework.
RUN dotnet publish src/ClaimPortal.Notifications/ClaimPortal.Notifications.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
    && rm -f /app/publish/*.pdb

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS final

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0

WORKDIR /app
COPY --from=build /app/publish .

# APP_UID (1654) and the matching `app` user are defined by the base image. Referencing it
# rather than hard-coding a uid keeps this correct if Microsoft moves it.
USER $APP_UID

# No HEALTHCHECK: this is a Worker SDK process with no HTTP surface, so there is nothing to
# probe from outside without adding a dependency. Nothing in the compose file depends_on the
# worker, so ordering does not need one. See the compose file for the ordering it does need.
ENTRYPOINT ["dotnet", "ClaimPortal.Notifications.dll"]
