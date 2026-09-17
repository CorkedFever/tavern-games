# Liar's Dice

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that lets
you play the bluffing dice game **Liar's Dice** with other players, plus the relay
server that connects them.

> ⚠️ Like all Dalamud plugins, this is a third-party tool and using it is against the
> FFXIV Terms of Service. Build and run at your own risk.

## How it works

Players don't talk to each other through the game. Each plugin opens a WebSocket to a
small **relay server**, which matchmakes players into rooms by a short code and acts as
the **authoritative game host** — it rolls the dice and only reveals hands when someone
calls a bluff, so a tampered client can't cheat about its own dice.

## Projects

| Project | Target | Role |
|---|---|---|
| `src/TavernGames.Core` | `net10.0` | Pure game engine + the shared network message contracts. No Dalamud. |
| `src/TavernGames.Server` | `net10.0` | ASP.NET Core WebSocket relay + authoritative game host. |
| `src/TavernGames.Plugin` | `net10.0-windows` | The Dalamud plugin: ImGui UI + network client. |
| `tests/TavernGames.Core.Tests` | `net10.0` | Unit tests for the engine. |
| `tests/TavernGames.Server.Tests` | `net10.0` | End-to-end game-flow tests through the real server. |

## Build

Requires the **.NET 10 SDK**. The plugin additionally needs the Dalamud dev libraries,
which XIVLauncher installs at `%AppData%\XIVLauncher\addon\Hooks\dev\` (override with the
`DalamudLibPath` MSBuild property).

```sh
dotnet build TavernGames.slnx -c Release
dotnet test
```

A successful Release build of the plugin produces a dev-installable folder at
`src/TavernGames.Plugin/bin/Release/LiarsDice/` (containing `latest.zip`).

## Run the server

```sh
# Listens on http://0.0.0.0:5050 by default; WebSocket endpoint is /ws.
dotnet run --project src/TavernGames.Server -c Release
# Override the address:
ASPNETCORE_URLS=http://0.0.0.0:8080 dotnet run --project src/TavernGames.Server -c Release
```

## Use the plugin

1. Point Dalamud's dev-plugin loader at the built `LiarsDice` folder.
2. In game, run `/liarsdice` to open the window.
3. Set the server URL (default `ws://localhost:5050/ws`), connect, then **Create Room**
   or **Join** with a 4-character code. The host starts the game once ≥2 players are in.

## Rules (Perudo-style common hand)

Everyone rolls their dice in secret. Going in turn, you either **raise** the standing bid
(more dice, or the same count of a higher face) or **call liar**. On a call, all hands are
revealed and the bid's face is counted across the whole table: if there are at least as
many as bid, the caller loses a die; otherwise the bidder does. Lose all your dice and
you're out. Last player standing wins.
