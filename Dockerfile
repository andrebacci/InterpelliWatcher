# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY InterpelliWatcher/ ./
RUN dotnet publish InterpelliWatcher.csproj -c Release -o /app

# Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

# Installa git e tzdata (serve per l'ora italiana nei log)
RUN apt-get update && apt-get install -y --no-install-recommends git tzdata && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Lo script di avvio (vedi run.sh): fa il checkout dello stato,
# esegue il watcher, poi committa i file aggiornati.
COPY run.sh /run.sh
RUN chmod +x /run.sh

CMD ["/run.sh"]
