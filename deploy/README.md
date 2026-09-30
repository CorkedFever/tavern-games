# The relay on meteor

The Tavern Games relay runs on meteor, the same box as Aetherstream's services, in its own
compose stack at `/opt/tavern-games`. Players connect to:

```
wss://tavern-games.corkedfever.com/party/ws
```

which is the plugin's default server.

## How it fits together

- **Caddy is not ours.** Meteor's Caddy belongs to corkedfever-website
  (`deploy/meteor/caddy/Caddyfile`, running at `/opt/corkedfever`). It owns ports 80 and 443,
  holds the certificates, and creates the `corkedfever` Docker network. Its
  `tavern-games.corkedfever.com` block sends `/party/*` to `tavern-relay:5050` with the `/party`
  prefix stripped, and everything else to the page on GitHub Pages. Route changes go through
  that repo, not this one.
- **The relay** is one container, `tavern-relay`, on the `corkedfever` network. It publishes no
  host ports, so only Caddy can reach it. Its SQLite database (profiles, venues, leaderboards)
  lives in the `tavern_data` volume and survives restarts and image updates.
- **The image is built by GitHub Actions**, never on meteor, which has one vCPU and too little
  free memory for a .NET SDK restore. `.github/workflows/relay-image.yml` runs the tests, then
  publishes `ghcr.io/corkedfever/tavern-relay:latest` and a `:sha-<commit>` tag whenever the
  server or the game rules change on `main`. The package is public, so meteor pulls it without
  credentials.

## First deploy

From this repo, on a machine with the meteor SSH key. corkedfever's stack must already be up,
since this one joins its network.

```sh
ssh -i ~/.ssh/meteor root@aetherstream.corkedfever.com "mkdir -p /opt/tavern-games"
scp -i ~/.ssh/meteor deploy/docker-compose.yml root@aetherstream.corkedfever.com:/opt/tavern-games/docker-compose.yml
ssh -i ~/.ssh/meteor root@aetherstream.corkedfever.com "cd /opt/tavern-games && docker compose pull && docker compose up -d"
```

Check it from inside the network before Caddy points at it:

```sh
ssh -i ~/.ssh/meteor root@aetherstream.corkedfever.com "docker run --rm --network corkedfever curlimages/curl -fsS http://tavern-relay:5050/health"
```

That prints `{"status":"ok"}`. Once corkedfever's Caddy has the `/party` route, the same check
works from anywhere as `https://tavern-games.corkedfever.com/party/health`.

## Updating

Push to `main`; the workflow publishes a new `:latest`. Then:

```sh
ssh -i ~/.ssh/meteor root@aetherstream.corkedfever.com "cd /opt/tavern-games && docker compose pull && docker compose up -d"
```

To go back to an earlier build, set the compose file's image to that build's `:sha-<commit>`
tag and run the same command.

## Watching it

```sh
ssh -i ~/.ssh/meteor root@aetherstream.corkedfever.com "docker logs --tail 100 -f tavern-relay"
```

## Knobs

Set in `docker-compose.yml` under `environment`:

| Variable | Default | What it does |
|---|---|---|
| `TAVERN_MAX_ROOMS` | 200 | Caps concurrent rooms. Empty rooms are freed on their own. |
| `Tavern__DbPath` | `/app/tavern-data/tavern.db` | Where the database lives. Keep it inside the volume. |

The container is limited to 256 MB (`mem_limit`), which .NET sizes its heap to. The box shares
its memory with Aetherstream's services.

## Backups

Everything the server knows is the one SQLite file in the `tavern_data` volume. To copy it off
the box:

```sh
ssh -i ~/.ssh/meteor root@aetherstream.corkedfever.com "docker cp tavern-relay:/app/tavern-data/tavern.db /root/tavern-backup.db"
scp -i ~/.ssh/meteor root@aetherstream.corkedfever.com:/root/tavern-backup.db .
```

## Running your own relay elsewhere

The server needs nothing but the .NET 10 runtime, or this image. `dotnet run --project
src/TavernGames.Server -c Release` serves `ws://localhost:5050/ws`; players point the plugin's
Setup at whatever address your own proxy gives it.
