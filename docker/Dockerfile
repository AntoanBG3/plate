# Build context is the repository root: docker build -f docker/Dockerfile .
# Base images are pinned by digest; refresh them monthly for security patches (see README, "Updating base images").
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src
COPY src/Plate.Core/Plate.Core.csproj src/Plate.Core/
COPY src/Plate.Web/Plate.Web.csproj src/Plate.Web/
RUN dotnet restore src/Plate.Web/Plate.Web.csproj -r linux-x64
COPY src/ src/
# The Git commit, passed from the Mac at deploy time (rsync sends no .git); reports and errors carry it.
ARG APP_VERSION=unknown
RUN dotnet publish src/Plate.Web/Plate.Web.csproj -c Release -o /app --no-restore -r linux-x64 --self-contained false -p:UseAppHost=false \
    -p:InformationalVersion=$APP_VERSION -p:IncludeSourceRevisionInInformationalVersion=false

# chiseled-extra includes ICU and time zone data (Europe/Sofia, Bulgarian text). No shell, no package manager.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:c8dd08a50d8ffd19bd8da72edee05a7ac15b486a5b1869cf46fdb370bf1fbc76
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Plate.Web.dll"]
