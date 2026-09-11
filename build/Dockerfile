# syntax=docker/dockerfile:1.7
# Multi-stage build for all Alert Hub backend images. Select the host with --build-arg HOST=Api|Ingest|Workers|Migrator.
ARG HOST=Api

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG HOST
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props AlertHub.sln ./
COPY src/ src/
RUN dotnet restore src/AlertHub.${HOST}/AlertHub.${HOST}.csproj
RUN dotnet publish src/AlertHub.${HOST}/AlertHub.${HOST}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG HOST
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0 \
    HOST_ASSEMBLY=AlertHub.${HOST}.dll
WORKDIR /app
COPY --from=build /app .
RUN groupadd -r alerthub && useradd -r -g alerthub -d /app alerthub && chown -R alerthub:alerthub /app
USER alerthub
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet ${HOST_ASSEMBLY} \"$@\"", "--"]
