# Build context is the repository root: docker build -f docker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GymTracker.Core/GymTracker.Core.csproj src/GymTracker.Core/
COPY src/GymTracker.Web/GymTracker.Web.csproj src/GymTracker.Web/
RUN dotnet restore src/GymTracker.Web/GymTracker.Web.csproj
COPY src/ src/
RUN dotnet publish src/GymTracker.Web/GymTracker.Web.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# chiseled-extra includes ICU and time zone data (Europe/Sofia, Bulgarian text). No shell, no package manager.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GymTracker.Web.dll"]
