# Counter Pick Master

Counter Pick Master is a lightweight WinForms app for finding League of Legends counterpicks using OP.GG data. It uses Gold Solo/Duo matches across all servers on the current patch.

## Getting started

1. Install the [Microsoft .NET Desktop Runtime 10 x64](https://dotnet.microsoft.com/download/dotnet/10.0) for Windows if needed.
2. Open `CounterPickMaster.exe` on Windows x64.
3. Run `Create-Desktop-Shortcut.cmd` to create a desktop shortcut with the app's OP.GG icon.

At startup, the app opens its saved Gold snapshot and checks the current OP.GG patch in the background. It downloads fresh matchup tables when the patch changes or the last download is at least 24 hours old. If the update fails, the saved tables stay available and you can retry. The local cache is stored at `%LOCALAPPDATA%\CounterPickMaster\matchups.json`.

Start typing a champion's English or Russian name. Click a suggestion or select it and press Enter. Only roles with matchup data for that champion are enabled. If the current role has no data, the app selects the first available role.

The app shows every available matchup in which the selected champion's win rate is below 50%. It sorts by win rate from lowest to highest, with more games first when rates are tied. Red text marks matchups with fewer than 100 games.

## Data sources

- [OP.GG](https://op.gg/lol/champions/illaoi/counters/top?tier=gold&region=global) provides the matchup tables and champion lists by role. The app uses `tier=gold`, `region=global`, and `type=ranked`.
- OP.GG's 160 × 160 champion portraits are stored in `Portraits` and embedded in the executable for offline display.
- [Riot Data Dragon](https://developer.riotgames.com/docs/lol#data-dragon) provides English and Russian champion names linked by champion ID.
- [Riot patch notes](https://www.leagueoflegends.com/en-us/news/tags/patch-notes/) provide the patch release date when available.

The executable includes a patch 16.23 snapshot so search works even on the first launch without a network connection.

## Build

```powershell
dotnet publish CounterPickMaster.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none
```
