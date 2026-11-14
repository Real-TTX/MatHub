# syntax=docker/dockerfile:1

# MatHub: the server, the Windows agent it hands out (built here, so both have the same version) and the boot loaders of the network boot.
#   docker build -t mathub:local .                         (local-<date>)
#   docker build --build-arg Channel=release --build-arg BuildNumber=42 --build-arg BuildDate=20261112 .

# ---------------------------------------------------------------------------
# The boot loaders: iPXE with MatHub's script built in (UEFI x64 and BIOS), and wimboot for Windows PE
# ---------------------------------------------------------------------------
# x86_64 code, built on the builder's own platform (the result is the same for the amd64 and the arm64 image)
FROM --platform=$BUILDPLATFORM debian:bookworm-slim AS netboot
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git build-essential gcc-multilib perl liblzma-dev binutils mtools \
 && rm -rf /var/lib/apt/lists/*
# pinned releases: the same loaders in every build (raise them on purpose)
ARG IpxeVersion=v2.0.0
ARG WimbootVersion=v2.9.0
WORKDIR /build
RUN git clone --depth 1 --branch "$IpxeVersion" https://github.com/ipxe/ipxe.git ipxe
COPY netboot/mathub-embed.ipxe ipxe/src/mathub.ipxe
RUN make -C ipxe/src -j"$(nproc)" NO_WERROR=1 EMBED=mathub.ipxe bin-x86_64-efi/ipxe.efi bin-x86_64-efi/snponly.efi bin/undionly.kpxe \
 && mkdir -p /netboot \
 && cp ipxe/src/bin-x86_64-efi/ipxe.efi ipxe/src/bin-x86_64-efi/snponly.efi ipxe/src/bin/undionly.kpxe /netboot/
# wimboot (Windows PE over HTTP): the released binary – an image without it fails to build
RUN curl -fsSL -o /netboot/wimboot "https://github.com/ipxe/wimboot/releases/download/$WimbootVersion/wimboot" && test -s /netboot/wimboot

# ---------------------------------------------------------------------------
# The Windows agent (one file with .NET inside)
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS agent
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatHub.Agent/MatHub.Agent.csproj", "src/MatHub.Agent/"]
RUN dotnet restore "src/MatHub.Agent/MatHub.Agent.csproj" -r win-x64
COPY src/MatHub.Shared src/MatHub.Shared
COPY src/MatHub.Agent src/MatHub.Agent
# the version is written beside the file: the server offers exactly this one to its agents
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatHub.Agent/MatHub.Agent.csproj" -c Release -r win-x64 -o /agent --no-restore \
      -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE && \
    dotnet msbuild "src/MatHub.Agent/MatHub.Agent.csproj" -getProperty:InformationalVersion \
      -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE | tr -d '[:space:]' > /agent/agent.version && \
    cat /agent/agent.version

# ---------------------------------------------------------------------------
# The server
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable (no native host), so the arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatHub/MatHub.csproj", "src/MatHub/"]
RUN dotnet restore "src/MatHub/MatHub.csproj"

COPY src/MatHub.Shared src/MatHub.Shared
COPY src/MatHub src/MatHub
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatHub/MatHub.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="MatHub" \
      org.opencontainers.image.description="Client-Management: Inventar, Softwareverteilung und Updates, Skripte (Code, Blöcke, Flows) und Rollout über das Netzwerk" \
      org.opencontainers.image.source="https://github.com/Real-TTX/MatHub"

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:4910 \
    HTTP_PORTS="" \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    TZ=Europe/Berlin

# 4910: the pages, the API, the agents and the HTTP part of the network boot; 4969/udp: TFTP (the compose file maps port 69 to it)
EXPOSE 4910
EXPOSE 4969/udp

COPY --from=build /app/publish .
COPY --from=agent /agent/mathub-agent.exe /agent/agent.version ./agent/
COPY --from=netboot /netboot ./netboot/

# One volume for what the app keeps in files: the keys that protect the sessions and the passwords of the profiles, the installers, the boot images.
RUN mkdir -p /data/keys && chown -R $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

# The app checks itself on /api/health (200 for ok and degraded, 503 for down), so the image needs no curl.
HEALTHCHECK --interval=30s --timeout=8s --start-period=30s --retries=3 \
    CMD ["dotnet", "MatHub.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatHub.dll"]
