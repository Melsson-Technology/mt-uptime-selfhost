# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────────────────────────
#  MT-Uptime container image.
#
#      docker build -f docker/Dockerfile -t mt-uptime .     <- context is the REPOSITORY ROOT
#
#  THE ONE THING THAT MUST BE RIGHT
#
#  /var/lib/mt-uptime holds the SQLite database AND the Data Protection keys, and they are not
#  separable. The keys decrypt every secret in the database — SendGrid credentials, monitored-database
#  passwords, webhook URLs — so a copy of one without the other is not a backup, it is scrap.
#
#  Mount a volume covering only the database and the container starts, migrates, and reports healthy
#  while unable to decrypt a single stored secret. Nothing errors; the secrets are simply gone. One
#  volume, the whole directory. docker/docker-compose.yml gets this right — copy from there.
# ─────────────────────────────────────────────────────────────────────────────────────────────────


# The SDK stage pins itself to the BUILDER's architecture and cross-publishes with `-a $TARGETARCH`.
# Emulating the whole SDK under QEMU to build arm64 works, but for .NET it is punishingly slow; this
# way a two-architecture build costs roughly what one costs.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore before copying sources, so editing a .cs file does not invalidate the restore layer. Only
# the host project and what it references are needed — the test project is not part of the image.
COPY global.json Directory.Build.props ./
COPY Core.MT-Uptime/Core.MT-Uptime.csproj Core.MT-Uptime/
COPY SelfHost.MT-Uptime/SelfHost.MT-Uptime.csproj SelfHost.MT-Uptime/
RUN dotnet restore SelfHost.MT-Uptime/SelfHost.MT-Uptime.csproj -a "$TARGETARCH"

COPY Core.MT-Uptime/ Core.MT-Uptime/
COPY SelfHost.MT-Uptime/ SelfHost.MT-Uptime/
# Deliberately NOT --no-restore, however tempting it looks next to the restore layer above.
#
# That restore runs when only the .csproj files exist. Framework-provided static web assets — chiefly
# _framework/blazor.web.js — are resolved as part of restore, and publishing with --no-restore skips
# re-evaluating them, so they are silently left out of wwwroot and out of the endpoints manifest.
#
# The failure is invisible until runtime and looks like anything but a build problem: blazor.web.js
# 404s, interactivity never starts, every interactive page degrades to a plain HTML form, and the
# first thing anyone tries — adding a monitor — POSTs to a route with no handler and returns
# 400 Bad Request. Static pages, the setup wizard and /healthz all keep working throughout, so a smoke
# test that only checks those will pass while the whole admin UI is broken.
#
# Restore here is cheap: the packages are already in the image layer above, so this re-resolves rather
# than re-downloads.
RUN dotnet publish SelfHost.MT-Uptime/SelfHost.MT-Uptime.csproj \
        -c Release -a "$TARGETARCH" -o /app


# ─────────────────────────────────────────────────────────────────────────────────────────────────

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is only here for HEALTHCHECK. The aspnet image ships without it or wget, and a health check
# whose command does not exist reports the container unhealthy forever — which on a monitoring tool
# is a particularly poor first impression.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# AGPL-3.0: ship the licence with the binary it covers.
COPY LICENSE /app/LICENSE

# Both storage paths inside one directory, so a single volume covers them. Program.cs creates them at
# startup when missing, so an empty volume needs no preparation.
ENV Storage__DatabasePath=/var/lib/mt-uptime/mt-uptime.db \
    Storage__DataProtectionKeysPath=/var/lib/mt-uptime/keys \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0

# Created and chowned here so a NAMED volume inherits this ownership when Docker initialises it.
# A BIND mount does not — Docker takes the host directory's ownership as-is — so a bind-mounted host
# directory has to be chowned to this UID yourself. See docker/README.md.
RUN mkdir -p /var/lib/mt-uptime \
    && chown "$APP_UID:$APP_UID" /var/lib/mt-uptime \
    && chmod 700 /var/lib/mt-uptime

# Declared so that forgetting `-v` costs you a stranded anonymous volume rather than the Data
# Protection keys. Losing those is unrecoverable: every stored secret becomes undecryptable, with a
# perfect copy of the database in hand.
VOLUME /var/lib/mt-uptime

USER $APP_UID
EXPOSE 8080

# start-period covers first boot, which runs EF migrations against an empty database.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "MT.Uptime.Web.dll"]
