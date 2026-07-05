# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY MotoRouteFinder.Core/ MotoRouteFinder.Core/
COPY MotoRouteFinder.Server/ MotoRouteFinder.Server/
COPY MotoRouteFinder.Web.sln .
RUN dotnet publish MotoRouteFinder.Server/MotoRouteFinder.Server.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app/publish .

ENV MAPS_DIR=/data/maps
ENV HOST=0.0.0.0
RUN mkdir -p /data/maps

VOLUME ["/data/maps"]

EXPOSE 5000
ENTRYPOINT ["dotnet", "MotoRouteFinder.Server.dll"]
