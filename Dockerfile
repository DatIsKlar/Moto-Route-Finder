# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MotoRouteFinder.Core/ MotoRouteFinder.Core/
COPY MotoRouteFinder.Server/ MotoRouteFinder.Server/
COPY MotoRouteFinder.Web.sln .
RUN dotnet publish MotoRouteFinder.Server/MotoRouteFinder.Server.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

RUN groupadd --system --gid 1001 appgroup && \
    useradd --system --uid 1001 --gid appgroup appuser

RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

ENV MAPS_DIR=/data/maps
ENV HOST=0.0.0.0
RUN mkdir -p /data/maps && chown appuser:appgroup /data/maps

VOLUME ["/data/maps"]

EXPOSE 5000
USER appuser
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://localhost:5000/api/route/health || exit 1
ENTRYPOINT ["dotnet", "MotoRouteFinder.Server.dll"]
