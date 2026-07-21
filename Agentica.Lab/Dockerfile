# The tag communicates the reviewed patch level; the digest makes the bytes immutable.
FROM mcr.microsoft.com/dotnet/runtime:10.0.10@sha256:ed5d539b27842d656a06a5984dbcb5114d3e885fbada612a49a5a7c3c3a44e1c AS base
WORKDIR /app
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Keep restore inputs in their own layer and require the checked-in dependency graph.
COPY ["global.json", "."]
COPY ["Directory.Build.props", "."]
COPY [".editorconfig", "."]
COPY ["NuGet.config", "."]
COPY ["Agentica/Agentica.csproj", "Agentica/"]
COPY ["Agentica/Packages.lock.json", "Agentica/"]
COPY ["Agentica.Clients/Agentica.Clients.csproj", "Agentica.Clients/"]
COPY ["Agentica.Clients/Packages.lock.json", "Agentica.Clients/"]
COPY ["Agentica.Lab/Agentica.Lab.csproj", "Agentica.Lab/"]
COPY ["Agentica.Lab/Packages.lock.json", "Agentica.Lab/"]
RUN dotnet restore "Agentica.Lab/Agentica.Lab.csproj" --locked-mode --configfile NuGet.config

COPY ["LICENSE", "."]
COPY ["README.md", "."]
COPY ["Agentica/", "Agentica/"]
COPY ["Agentica.Clients/", "Agentica.Clients/"]
COPY ["Agentica.Lab/", "Agentica.Lab/"]
RUN dotnet publish "Agentica.Lab/Agentica.Lab.csproj" \
    --configuration $BUILD_CONFIGURATION \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false \
    /p:ContinuousIntegrationBuild=true

FROM base AS final
LABEL org.opencontainers.image.source="https://github.com/zekenaulty/Agentica" \
      org.opencontainers.image.title="Agentica Lab" \
      org.opencontainers.image.description="Internal research harness; not a supported product CLI" \
      org.opencontainers.image.licenses="LicenseRef-Agentica-Source-Available-1.0"
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Agentica.Lab.dll"]
