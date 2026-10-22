# syntax=docker/dockerfile:1.7
# Multi-stage build for all DeNoise backend images. Select the host with --build-arg HOST=Api|Ingest|Workers|Migrator.
# The Api image also carries the React SPA (web/) in wwwroot and serves it from the same origin as the API, which the
# session cookie, CSRF and SSE assume. For the other hosts web-build installs and builds nothing and /out stays empty
# (stage names cannot be selected by HOST: references must be lowercase and HOST keeps the project-name casing).
ARG HOST=Api

FROM node:22-alpine AS web-build
ARG HOST
WORKDIR /web
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml web/.npmrc ./
RUN mkdir -p /out && if [ "$HOST" = "Api" ]; then npm install -g pnpm@10 && pnpm install --frozen-lockfile; fi
COPY web/ ./
RUN if [ "$HOST" = "Api" ]; then pnpm build && cp -r dist /out/wwwroot; fi

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG HOST
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props DeNoise.sln ./
COPY src/ src/
RUN dotnet restore src/DeNoise.${HOST}/DeNoise.${HOST}.csproj
RUN dotnet publish src/DeNoise.${HOST}/DeNoise.${HOST}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG HOST
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0 \
    HOST_ASSEMBLY=DeNoise.${HOST}.dll
WORKDIR /app
COPY --from=build /app .
# wwwroot/ for HOST=Api, nothing for the other hosts.
COPY --from=web-build /out/ ./
RUN groupadd -r denoise && useradd -r -g denoise -d /app denoise && chown -R denoise:denoise /app
USER denoise
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet ${HOST_ASSEMBLY} \"$@\"", "--"]
