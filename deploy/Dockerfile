# The Tavern Games relay server as a small runtime image.
# Build context is the REPO ROOT, so the Core project reference resolves.
#
# Built by .github/workflows/relay-image.yml and published as ghcr.io/corkedfever/tavern-relay.
# Nothing builds on meteor: it has one vCPU and too little memory for a .NET SDK restore.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, cached unless a .csproj changes.
COPY src/TavernGames.Core/TavernGames.Core.csproj src/TavernGames.Core/
COPY src/TavernGames.Server/TavernGames.Server.csproj src/TavernGames.Server/
RUN dotnet restore src/TavernGames.Server/TavernGames.Server.csproj

COPY src/TavernGames.Core/ src/TavernGames.Core/
COPY src/TavernGames.Server/ src/TavernGames.Server/
RUN dotnet publish src/TavernGames.Server/TavernGames.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The database folder exists in the image and belongs to the non-root user, so a fresh named
# volume mounted over it starts out with that ownership and the server can write to it.
RUN mkdir -p /app/tavern-data && chown app:app /app/tavern-data
USER app

# Listen on 5050 inside the container; meteor's Caddy reaches it over the corkedfever network.
ENV ASPNETCORE_URLS=http://0.0.0.0:5050
EXPOSE 5050

ENTRYPOINT ["dotnet", "TavernGames.Server.dll"]
