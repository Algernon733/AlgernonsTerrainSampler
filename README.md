Refer to https://mods.vintagestory.at/algernonsterrainsampler for info about this mod

## What it gives other mods

- `TerrainSamplerMod.SampleColumn` and `GetBlockColumnHeight`: a column's terrain height, climate, rainfall,
  temperature, forest and shrub density, anywhere in the world, without generating the chunk. Server side.
  `SampleColumnWithTopRock` (since 2.0.0) adds the column's top rock (`TerrainColumnSample.TopRock`, the block code of
  the rock under its soil, from vanilla's rock strata).
- The terrain tile service (since 2.0.0): the same samples as fixed tiles, made once on the server, cached in memory
  and on disk, and sent to clients over the mod's own channel, so a client mod can show terrain far from the player on
  a dedicated server. `TerrainTileSystem.ServerInstance` or `ClientInstance`, then `RequestTile`.
  [docs/tile-service.md](docs/tile-service.md) has the design, the API and the settings.

## For server operators

The tile service is on by default: any player whose client mods ask can have the terrain, climate and top rock of land
round them, out to 32 km, including land nobody has explored. `ModConfig/algernonsterrainsampler.json` turns it off for
clients, requires a privilege, limits how far and how much, and sizes its caches (1 GiB on disk for every world
together) and its bandwidth (docs/tile-service.md, "Settings"). `/terrainsampler tiles` shows its state, and
`/terrainsampler mapcheck` and `rockcheck` compare what the sampler answers with the chunks the world has generated.

## Building

You'll need to create a file called "Directory.Build.props" to link the project to your Vintage Story folder.

Copypaste this into it and put in your VS directory:

```xml
<Project>
  <PropertyGroup>
    <!-- Set this to your Vintage Story installation folder -->
    <GameDirectory>C:\SomeFolder\AnotherFolder\VintageStoryFolderHere</GameDirectory>
  </PropertyGroup>
</Project>
```

The tests (`AlgernonsTerrainSampler.Tests`, xunit) need no running game, only that folder: `dotnet test`.
