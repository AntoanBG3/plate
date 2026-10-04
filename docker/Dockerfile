# Build context is the repository root: docker build -f docker/Dockerfile .
# Base images are pinned by digest; refresh them monthly for security patches (see README, "Updating base images").
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY src/GymTracker.Core/GymTracker.Core.csproj src/GymTracker.Core/
COPY src/GymTracker.Web/GymTracker.Web.csproj src/GymTracker.Web/
RUN dotnet restore src/GymTracker.Web/GymTracker.Web.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/GymTracker.Web/GymTracker.Web.csproj -c Release -o /app --no-restore -r linux-x64 --self-contained false -p:UseAppHost=false

# chiseled-extra includes ICU and time zone data (Europe/Sofia, Bulgarian text). No shell, no package manager.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GymTracker.Web.dll"]
