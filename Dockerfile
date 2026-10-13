# The demo image (spec 1.9, PHASE-7): the app in demo mode, run by docker-compose.yml.
# No secret is baked in: .dockerignore keeps .env out, and demo mode reads none.
# Images are pinned by exact tag (RULE-DEPENDENCIES): the SDK of global.json builds, the runtime
# the packages are pinned to runs (spec 6.2).

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# Restore first, from the files that decide it, so a code change does not repeat the restore.
# .editorconfig carries the analyzer settings the build enforces (generated migrations, D-022).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Exchange.Domain/Exchange.Domain.csproj src/Exchange.Domain/
COPY src/Exchange.Api/Exchange.Api.csproj src/Exchange.Api/
RUN dotnet restore src/Exchange.Api/Exchange.Api.csproj

COPY src/ src/
# No debug symbols in the runtime image.
RUN dotnet publish src/Exchange.Api/Exchange.Api.csproj --configuration Release --no-restore --output /app -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
COPY --from=build /app .

# The app listens on its own fixed port (spec 1.11); the runtime image's default port setting
# would only make Kestrel warn that it is overridden.
ENV ASPNETCORE_HTTP_PORTS=

# The image's own non-root user: the app never runs as root.
USER $APP_UID

EXPOSE 8000
# One process: demo mode applies the migrations at startup and keeps its keys in memory (D-008).
ENTRYPOINT ["dotnet", "Exchange.Api.dll"]
