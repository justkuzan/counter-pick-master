# Counter Pick Master

Counter Pick Master is a lightweight WinForms app for finding League of Legends counterpicks using OP.GG data. It uses Gold Solo/Duo matches across all servers on the current patch.

## Getting started

1. Install the [Microsoft .NET Desktop Runtime 10 x64](https://dotnet.microsoft.com/download/dotnet/10.0) for Windows if needed.
2. Open `CounterPickMaster.exe` on Windows x64.
3. Run `Create-Desktop-Shortcut.cmd` to create a desktop shortcut with the app's OP.GG icon.

At startup, the app checks the current OP.GG patch. It downloads fresh matchup tables when the patch changes or the last download is at least 24 hours old. If the network is unavailable, you can retry or open the saved Gold snapshot. The local cache is stored at `%LOCALAPPDATA%\CounterPickMaster\matchups.json`.

Choose a role and start typing a champion's English or Russian name. Click a suggestion or select it and press Enter. The selected champion stays active when you switch roles.

The app shows every available matchup in which the selected champion's win rate is below 50%. It sorts by win rate from lowest to highest, with more games first when rates are tied. Red text marks matchups with fewer than 100 games.

## Data sources

- [OP.GG](https://op.gg/lol/champions/illaoi/counters/top?tier=gold&region=global) provides the matchup tables and champion lists by role. The app uses `tier=gold`, `region=global`, and `type=ranked`.
- OP.GG's 160 × 160 champion portraits are stored in `Portraits` and embedded in the executable for offline display.
- [Riot Data Dragon](https://developer.riotgames.com/docs/lol#data-dragon) provides English and Russian champion names linked by champion ID.
- [Riot patch notes](https://www.leagueoflegends.com/en-us/news/tags/patch-notes/) provide the patch release date when available.

The executable includes a patch 16.23 snapshot that can be opened on the first launch without a network connection. When the app refreshes the snapshot, it downloads matchup tables for all five roles before enabling search.

## Build

```powershell
dotnet publish CounterPickMaster.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=none
```
