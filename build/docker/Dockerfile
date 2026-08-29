# One Dockerfile, several images, selected with --target.
#
# The alternative — a file per host — duplicates the base images, the restore
# layer, and every hardening decision, and the copies drift. What actually
# differs between the API, the worker, and the fake upstreams is one project path
# and one entry point, so those are the only things stated per target.

ARG DOTNET_VERSION=10.0

# ── restore ──────────────────────────────────────────────────────────────────
#
# Project files and sources for src/ and tools/ only. Test projects are never
# copied: they pull in Testcontainers and a test runner, none of which belongs in
# a deployed artifact, and they are a large share of the dependency graph.
#
# Restore is per project rather than over the solution, because the solution
# references the test projects and would fail on files that are deliberately not
# here.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS restore
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/ ./src/
COPY tools/ ./tools/

RUN dotnet restore src/Relay.Api/Relay.Api.csproj \
    && dotnet restore src/Relay.Worker/Relay.Worker.csproj \
    && dotnet restore tools/Relay.FakeProviders/Relay.FakeProviders.csproj

# ── publish ──────────────────────────────────────────────────────────────────

FROM restore AS publish-api
RUN dotnet publish src/Relay.Api/Relay.Api.csproj --no-restore -c Release -o /app

FROM restore AS publish-worker
RUN dotnet publish src/Relay.Worker/Relay.Worker.csproj --no-restore -c Release -o /app

FROM restore AS publish-fakes
RUN dotnet publish tools/Relay.FakeProviders/Relay.FakeProviders.csproj --no-restore -c Release -o /app

# ── migration bundle ─────────────────────────────────────────────────────────
#
# A self-contained executable built from the migrations in the assembly. It
# carries no SDK, no source, and no `dotnet ef`, which is what makes it runnable
# in a container that has none of those — and what lets migrations be a
# deployment step rather than something the application does to itself on startup
# (ADR 0015).
FROM restore AS bundle
RUN dotnet tool install --global dotnet-ef --version 10.0.11
ENV PATH="${PATH}:/root/.dotnet/tools"

# Restored again for the target runtime. A self-contained bundle needs assets for
# net10.0/linux-x64 specifically, and the earlier restore produced only the
# portable ones — so this fails without it, on a message that names the assets
# file rather than the missing runtime identifier.
RUN dotnet restore src/Relay.Infrastructure/Relay.Infrastructure.csproj --runtime linux-x64

RUN dotnet ef migrations bundle \
    --project src/Relay.Infrastructure/Relay.Infrastructure.csproj \
    --configuration Release \
    --self-contained \
    --runtime linux-x64 \
    --output /app/migrate \
    --force

# ── runtime base ─────────────────────────────────────────────────────────────
#
# Chiseled: no shell, no package manager, no busybox. There is nothing to exec
# into and nothing for an attacker to pivot with. It also removes most of what a
# vulnerability scanner would otherwise report, because almost all of that comes
# from OS packages the application never calls.
#
# The cost is real: no shell means no `docker exec` to look around, so
# diagnostics have to come from logs, metrics, and traces. That is a reason to
# have built those first.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS runtime
WORKDIR /app

# Non-root. The chiseled images ship a UID for it; running as root in a container
# is running as root on the host kernel for anything that escapes the namespace.
USER $APP_UID

ENV DOTNET_gcServer=1

# ── images ───────────────────────────────────────────────────────────────────

FROM runtime AS api
COPY --from=publish-api /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/Relay.Api.dll"]

FROM runtime AS worker
COPY --from=publish-worker /app .
ENTRYPOINT ["dotnet", "/app/Relay.Worker.dll"]

FROM runtime AS fakes
COPY --from=publish-fakes /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/Relay.FakeProviders.dll"]

# The bundle is self-contained, so it needs no .NET runtime at all — only a libc.
# Nothing else in the image, which is appropriate for something that holds DDL
# rights on the database.
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}-noble-chiseled AS migrate
WORKDIR /app
USER $APP_UID
COPY --from=bundle /app/migrate ./migrate
ENTRYPOINT ["/app/migrate"]
