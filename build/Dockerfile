# syntax=docker/dockerfile:1.7
# Multi-stage build for all DeNoise backend images. Select the host with --build-arg HOST=Api|Ingest|Workers|Migrator.
ARG HOST=Api

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
RUN groupadd -r denoise && useradd -r -g denoise -d /app denoise && chown -R denoise:denoise /app
USER denoise
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet ${HOST_ASSEMBLY} \"$@\"", "--"]
