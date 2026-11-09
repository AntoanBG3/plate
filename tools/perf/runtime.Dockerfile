# Usage after publishing: docker build -f tools/perf/runtime.Dockerfile -t plate-perf-arm64 /private/tmp/plate-perf-publish
# Measures native arm64 runtime-image footprint; source publishing is documented in the audit.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd
WORKDIR /app
COPY . .
ENTRYPOINT ["dotnet", "Plate.Web.dll"]
