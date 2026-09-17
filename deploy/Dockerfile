# Builds the Liar's Dice relay server into a small runtime image.
# Build context is the REPO ROOT (so the Core project reference resolves).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless a .csproj changes).
COPY src/LiarsDice.Core/LiarsDice.Core.csproj src/LiarsDice.Core/
COPY src/LiarsDice.Server/LiarsDice.Server.csproj src/LiarsDice.Server/
RUN dotnet restore src/LiarsDice.Server/LiarsDice.Server.csproj

# Build & publish.
COPY src/ src/
RUN dotnet publish src/LiarsDice.Server/LiarsDice.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Listen on 5050 inside the container; Caddy reaches it over the compose network.
ENV ASPNETCORE_URLS=http://0.0.0.0:5050
EXPOSE 5050

ENTRYPOINT ["dotnet", "LiarsDice.Server.dll"]
