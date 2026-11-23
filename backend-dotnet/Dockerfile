# Office Attendance Tracker API (.NET). Build from backend-dotnet/:
#   docker build -t ats-api backend-dotnet
#   docker run --rm -p 127.0.0.1:8001:8001 --env-file <your env file> ats-api
# Settings are the same keys as backend-dotnet/.env.example, passed as environment variables (or mount a file at
# /app/.env). Nothing secret is baked into the image. Database commands run in the same image:
#   docker run --rm --env-file <your env file> ats-api schema-check      (also: migrate, ef-baseline, seed, create-admin)

# The SDK matches backend-dotnet/global.json; the runtime is the ASP.NET Core patch that SDK ships with.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.404-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.15-noble

FROM ${SDK_IMAGE} AS build
WORKDIR /src
# Restore first, from the project files only, so the package layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ATS.Domain/ATS.Domain.csproj src/ATS.Domain/
COPY src/ATS.Application/ATS.Application.csproj src/ATS.Application/
COPY src/ATS.Infrastructure/ATS.Infrastructure.csproj src/ATS.Infrastructure/
COPY src/ATS.Api/ATS.Api.csproj src/ATS.Api/
RUN dotnet restore src/ATS.Api/ATS.Api.csproj
COPY config/ config/
COPY src/ src/
RUN dotnet publish src/ATS.Api/ATS.Api.csproj -c Release --no-restore -o /app -p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS runtime
# The Ubuntu (noble) ASP.NET image includes ICU and tzdata. Both are needed: globalization is on
# (InvariantGlobalization=false) and OFFICE_TIMEZONE is an IANA zone, which the API checks at startup.
ENV ATS_HOME=/app \
    URLS=http://+:8001 \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true
WORKDIR /app
COPY --from=build /app .
# The image's built-in non-root user. A SQLite database or a report logo must be mounted readable (and the SQLite
# directory writable) by this user; production uses PostgreSQL.
USER $APP_UID
EXPOSE 8001
# Liveness: GET /api/health. Readiness (database reachable): GET /api/health/db (503 when it is not).
ENTRYPOINT ["dotnet", "ATS.Api.dll"]
