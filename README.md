# Conditional Medians

A Cities: Skylines II mod that closes the opening in a divided road's built-in median at a junction. The road's own median runs through the junction, car turns across it leave the route network, and the crosswalks stay. On a road with a painted center line instead, the line is continued through the junction and the same turns are removed.

[Install from Paradox Mods](https://mods.paradoxplaza.com/mods/168662/Windows)

## In game

Forbid the movements across the median first, with the game's turn signs or Traffic's lane connector. Then click the round blue median button in the top-left row of mod buttons, or press F8, and click the junction. Blue means it can be closed, orange that it is closed (click to reopen), and red that it cannot be closed yet; the panel says why.

## Build

Needs Cities: Skylines II (built against 1.6.4f1), a .NET SDK with the Roslyn compiler, and Node.js for the UI module.

```powershell
powershell -ExecutionPolicy Bypass -File .\CS2\build.ps1
```

The script finds the game through `CSII_INSTALLATIONPATH`, which the game sets when it installs its modding toolchain, else in Steam's default library; pass `-Game` otherwise. The UI module needs the Cities: Skylines II UI template packages: run `npm ci` in `CS2\UI` once. The build writes `dist\CS2`; with the game closed, copy its four files to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\ConditionalMedians2\`.

`dotnet run --project CS2/Tests/GeometryChecks.csproj` runs the geometry checks (needs .NET 8; pass `-p:GameManaged=<game folder>\Cities2_Data\Managed` when the game's toolchain has not set `CSII_MANAGEDPATH`). [CS2/README.md](CS2/README.md) has the full developer notes.

## Layout

- `CS2/Source`: the mod
- `CS2/UI`: the game UI module (the tool's button and panel); `CS2/UI/types` comes unchanged from the official Cities: Skylines II UI mod template
- `CS2/Tests`: geometry checks
- `CS2/Properties`: the Paradox Mods page; `CS2/publish.ps1` uploads it
