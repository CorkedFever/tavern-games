# Tavern Games

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that puts
a table of tavern games in your game window, plus the relay server that connects
players. Open a room, share the code, fill empty seats with bots, and let onlookers
spectate.

**Games so far:** Liar's Dice, Pig.

> Like all Dalamud plugins, this is a third-party tool and using it is against the
> FFXIV Terms of Service. Build and run at your own risk. Wagers are flavor only: the
> plugin never touches gil.

## How it works

Players don't talk to each other through the game. Each plugin opens a WebSocket to a
small **relay server**, which matchmakes players into rooms by a short code and is the
**authoritative host** for every game: it rolls the dice, keeps hidden state hidden, and
only tells each seat what that seat is allowed to see. A tampered client can't cheat.

The server and plugin are split into a **platform** and **games**:

- The platform owns everything games share: rooms and codes, seating, bots, spectators,
  pacing, delivery, the lobby UI, the log, and chat-log narration.
- A game is a server-side `IGameModule` (rules, bot brain, its own wire messages) and a
  plugin-side `IClientGame` (state, log wording, narration, table view).

## Profiles and venues

- **Profiles.** The first time a plugin connects, the server issues it a secret token
  and the plugin keeps it in its config. The same token brings the same profile back,
  so there are no passwords; the server stores only the token's hash. A profile has a
  display name, a tagline, and a lifetime record per game. Players can delete their
  profile from the Profile tab. Guests (no profile) can still play private tables.
- **Venues.** Anyone with a profile can start a venue and hand out its join code.
  Roles are member, staff and owner. Staff host tables for the venue, rotate the join
  code and remove members; the owner also manages roles, edits or deletes the venue,
  and can hand it over. A venue's tables seat members only (anyone may spectate) and
  are listed on the venue page, so members join without needing a room code.
- **Leaderboards.** Every finished game is recorded for the profiles seated at the
  start. A venue's leaderboard counts only games with two or more real players, so
  wins against bots can't be farmed. A player's own record counts everything.

All of this lives in one SQLite file (`Tavern:DbPath`, default `data/tavern.db` next to
the server; set env `Tavern__DbPath` to move it). The schema is a list of numbered
scripts in `TavernDb`, applied on startup, so a new build upgrades an existing file.
Back that file up and you've backed up everything.

## Projects

| Project | Target | Role |
|---|---|---|
| `src/TavernGames.Core` | `net10.0` | Platform contracts, wire protocol, and every game's rules. No Dalamud. |
| `src/TavernGames.Server` | `net10.0` | ASP.NET Core WebSocket relay and authoritative host. Game-agnostic. |
| `src/TavernGames.Plugin` | `net10.0-windows` | The Dalamud plugin: ImGui shell, network client, per-game views. |
| `tests/TavernGames.Core.Tests` | `net10.0` | Engine, bot, platform and wire-format tests. |
| `tests/TavernGames.Server.Tests` | `net10.0` | End-to-end games played through the real server. |

## Adding a game

1. **Rules** in `src/TavernGames.Core/Games/<Name>/`: a plain engine class, its message
   records, and an `IGameModule` that turns moves into `Emit`s (`ToAll`, `ToPlayer`,
   `Pause`). Give it a static `GameDescriptor` (name, player counts, room options, and
   its message types with namespaced wire names like `mygame.move`).
2. **Register it** by adding the descriptor to `GameCatalog.Games`. The room, the
   serializer and the lobby's game picker and option sliders pick it up from there.
3. **Plugin view** in `src/TavernGames.Plugin/Games/`: an `IClientGame`, added to the
   list in `Plugin.cs`.
4. **Tests**: engine rules, a bot fuzz (every bot move must be legal and games must
   end), and one end-to-end game in `TavernGames.Server.Tests`.

No platform code changes. Pig was added this way as the proof.

## Build

Requires the **.NET 10 SDK**. The plugin additionally needs the Dalamud dev libraries,
which XIVLauncher installs at `%AppData%\XIVLauncher\addon\Hooks\dev\` (override with the
`DalamudLibPath` MSBuild property).

```sh
dotnet build TavernGames.slnx -c Release
dotnet test
```

## Run the server

```sh
# Listens on port 5050; the WebSocket endpoint is /ws and /health reports status.
dotnet run --project src/TavernGames.Server -c Release
```

Environment knobs: `TAVERN_MAX_ROOMS` (default 200) caps concurrent rooms;
`TAVERN_BOT_DELAY_MS` overrides bot pacing (tests set it to 0). See `deploy/` for
running it in Docker behind Caddy.

## Use the plugin

1. In `/xlsettings` → Experimental → Dev Plugin Locations, add
   `src/TavernGames.Plugin/bin/Release/TavernGames.dll`, then enable it in `/xlplugins`.
2. In game, run `/tavern`.
3. Set the server URL (default `ws://localhost:5050/ws`) and connect. On the **Play**
   tab pick a game and **Create Room**, or **Join** / **Spectate** with a 4-character
   code. The host can add bots and starts the game.
4. The **Venues** tab is where you join a venue by code or start your own, see its open
   tables, leaderboard and members. Staff get a "Host for" picker on the Play tab.
5. The **Profile** tab holds your name, tagline and record.

## The games

**Liar's Dice** (2-6 players). Everyone rolls in secret. In turn you raise the standing
bid (more dice, or the same count of a higher face). Anyone except the bidder can call
liar at any time: all hands are revealed and the face is counted across the table. If
the bid holds, the caller loses a die; if not, the bidder does. Last player with dice
wins.

**Pig** (2-6 players). On your turn roll one die as often as you dare, adding each roll
to the pot. Hold to bank the pot; roll a 1 and the pot is lost. First to the target
score (default 100) wins.
