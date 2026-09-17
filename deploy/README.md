# Deploying the Liar's Dice relay to a DigitalOcean droplet

This hosts the relay 24/7 behind Caddy (automatic HTTPS), so players connect to
`wss://liarsdice.<yourdomain>/ws` and your home IP is never involved. Caddy is set
up so you can host **other apps** on the same droplet later with ~3 extra lines.

## 1. Buy a domain & plan the subdomain
Register a domain (Namecheap, Porkbun, Cloudflare ~$10/yr). You'll use a subdomain
for the relay, e.g. **`liarsdice.yourdomain.com`**.

## 2. Create the droplet
- DigitalOcean → **Create → Droplet**
- **Ubuntu 24.04 LTS**, **Basic / Regular, 2 GB RAM** ($12/mo)
- **Authentication: SSH key** (add yours; avoid password login)
- Create, and note the droplet's **public IP**.

## 3. Point DNS at the droplet
At your domain registrar, add an **A record**:
| Type | Host | Value |
|---|---|---|
| A | `liarsdice` | `<droplet IP>` |

(Optional: an A record for `*` (wildcard) → the IP, so future subdomains just work.)
DNS can take a few minutes to propagate.

## 4. SSH in and install Docker
```sh
ssh root@<droplet IP>

# Docker + compose plugin
curl -fsSL https://get.docker.com | sh

# Firewall: allow SSH + web only
ufw allow OpenSSH
ufw allow 80
ufw allow 443
ufw --force enable
```

## 5. Get the code and set your domain
```sh
git clone <your repo url> liarsdice
cd liarsdice/deploy

# Put your real subdomain in the Caddyfile (replace liarsdice.example.com)
nano Caddyfile
```

## 6. Launch
```sh
docker compose up -d --build
```
First run builds the image and Caddy fetches a TLS certificate (needs DNS from
step 3 to be live). Check it:
```sh
docker compose ps
docker compose logs -f caddy     # watch for the cert being issued
curl https://liarsdice.yourdomain.com/health   # -> {"status":"ok"}
```

## 7. Point the plugin at it
In the Liar's Dice window, set **Server** to:
```
wss://liarsdice.yourdomain.com/ws
```
Connect, create a room, and share the room code with friends (they set the same
server URL). Done — no tunnel, no home IP, always on.

## Updating after code changes
```sh
cd liarsdice && git pull
cd deploy && docker compose up -d --build
```

## Adding another app later
1. Add its service to `docker-compose.yml` on the `web` network (no published ports).
2. Add a block to the `Caddyfile`:
   ```
   myapp.yourdomain.com {
       reverse_proxy myapp:PORT
   }
   ```
3. Add the DNS A record for `myapp`, then `docker compose up -d`.

## Notes / hardening
- Rooms are capped (`LIARSDICE_MAX_ROOMS`, default 200) and empty rooms are freed
  automatically. There's no account system — anyone with the URL can create rooms,
  which is fine for casual play. Room codes are 4 characters.
- Consider creating a non-root user and disabling root SSH for extra safety.
- `docker compose logs -f relay` shows connection activity.
