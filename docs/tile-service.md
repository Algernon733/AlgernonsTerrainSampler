# The terrain tile service

Since 2.0.0 the sampler can serve its samples to other mods as cached tiles, on the server and, over its own network
channel, on clients. A mod that wants the terrain far from any loaded chunk (a far-terrain renderer, a map that shows
unexplored land, a horizon) asks for tiles; the sampler makes each tile once, keeps it, and hands it to everyone who
asks.

## Why

The sampler only runs on the server. Until now every mod that wanted its samples on a client built its own bridge: a
channel of its own, its own sampling on the server, its own cache. Farseer, Fast Map and VSPT each did. A server that
wanted all three installed three server halves, each sampling the same ground again and keeping its own copy, and a
player on a server that ran only the sampler got nothing, which is most servers.

## Tiles

A tile is always 128 cells across: 129 x 129 point samples, one every *step* blocks. The last row and column lie on the
next tile's first, so neighbouring tiles agree along the edge they share. A tile spans 128 x *step* blocks and is aligned
to that span. Its key is `(step, tileX, tileZ)`, and its corner is `(tileX * 128 * step, tileZ * 128 * step)`.

| Step | Tile span, blocks | Typical use |
| --- | --- | --- |
| 1 | 128 | Exact ground by the loaded edge |
| 2 | 256 | The taps for a prefiltered step-4 height |
| 4 | 512, one map region | A far field, a map's unexplored land |
| 8 | 1,024 | |
| 16 | 2,048 | |
| 32 | 4,096 | The horizon |
| 64 | 8,192 | A world overview |

Every tile costs the same: 16,641 columns, roughly half a second of one core. A tile is made row by row, so a row band's
map regions and chunk contexts stay in the sampler's own caches while it is crossed.

**Point samples only.** Filtering is the consumer's business. The step-2 tiles under a step-4 tile hold exactly the
3 x 3 taps a box filter averages for a step-4 height, and a consumer that needs samples outside its area (for normals,
say) reads them from the neighbouring tiles. Samples past the world's edge are clamped to it; a tile that starts outside
the world is refused.

### Fields

| Field | Type | |
| --- | --- | --- |
| `Height` | `short[]` | `TerrainColumnSample.Height` |
| `ClimateR`, `ClimateG`, `ClimateB` | `byte[]` | The raw climate map: temperature, rainfall, geologic activity. `ClimateColor(i)` packs them |
| `Rainfall` | `byte[]` | Height-adjusted rainfall times 255 (it is an integer over 255 already, so exact) |
| `Forest`, `Shrub` | `ushort[]` | The densities times 65,535 |
| `Rock`, `RockCodes` | `byte[]`, `string[]` | The top rock (`TerrainColumnSample.TopRock`): an index into the tile's own palette of block codes, entry 0 the empty code for not known. `TopRock(i)` reads it |

`TerrainTile.Column(i)` gives a column back as a `TerrainColumnSample`. Index `i` is `iz * 129 + ix`.

### The top rock

`TerrainColumnSample.TopRock` is the block code of the rock at the top of the column, under its soil, sand or gravel:
what vanilla's rock strata put there (`GenRockStrataNew`), which is also the rock worldgen's block layers take their
type from (`sand-{rocktype}`, `rock-{rocktype}`). Everything vanilla reads for it is seeded, so the sampler works it out
for any column, generated or not (`RockStrataSampler`): the region's geologic provinces (`GenMaps`' province layer,
seed + 3, smoothed into weights as vanilla smooths them), each stratum's noise map (`worldgen/rockstrata.json` through
the asset system, so mods' patches to it count), the distortion noises, and vanilla's walk down the column
(`RockColumn`, held by a test to a line-for-line copy of vanilla's loop). The instances are the sampler's own, not the
game's, which its worldgen thread uses meanwhile.

Tiles carry it, and so does `SampleColumnWithTopRock`; `SampleColumn` leaves it empty, as before 2.0.0, so that its
callers do not pay for it. A world with no vanilla strata generator gets an empty code. A mod that replaces vanilla's
strata generator still gets vanilla's answer here: an adapter for it belongs in `RockStrataSampler`, so that the
sampler's consumers need not change. `/terrainsampler rockcheck` compares the sampled rock with the generated chunks
round you.

### The format

The same bytes are written to disk and sent on the wire, so a cached tile goes out without being decoded. A 54-byte
header (magic `ATS1`, format version, step, tile X and Z, cells, field mask, payload length, SHA-256 of the payload),
then Brotli over the field planes. Each value is stored as its difference from the sample to its left, or above it at
the start of a row, and 16-bit planes are split into low and high bytes, so smooth ground compresses to very little.
Brotli is part of .NET, so the format adds no DLL. The rock plane comes after the others (field mask `0xFF`), and the
rock palette after that: a count (0 for 256), then each code as a length byte and its UTF-8. A tile whose version or
field mask is not this build's is made again.

## The sampler's map generators

The sampler reads the climate, upheaval, ocean, landform, forest and shrub maps of each region. Up to 1.3.0 it used the
game's own generators for them, the ones vanilla's `GenMaps` keeps. The game uses those from its worldgen thread
without a lock: it sets the forest and shrub layers' input map and then generates, and its climate and landform layers
share a climate noise that keeps a position seed between draws. The sampler's lock could not keep the game out, so a
sample taken while the game generated a region could come out wrong, or spoil the forest or shrub map the game saved.
The tile service samples all the time on background threads, which would have made that likely.

So the sampler now makes its own copies (`SamplerGenMaps`), from the seed and the world configuration as
`GenMaps.initWorldGen` makes the game's, when the world generator starts. The landform layer is its own port of
vanilla's (`SamplerLandformLayer`), over the sampler's own landforms: vanilla's picks a landform by writing weights into
the game's static landforms, which a copy of vanilla's class would still share with the game's worldgen thread.

To keep following a terrain mod that puts its own generator in the game's place, each of the game's generators and its
copy then generate the same test regions, and a generator whose type or maps differ is followed as before. That is done
when the world generator starts and again, finally, when the game starts running: after every mod's world generator
has started and before the game's chunk threads and the tile service, so the game's generators are safe to call and a
sample never mixes two generators. The server log says then which are followed, and that those are not thread safe.
`/terrainsampler mapcheck` compares the sampler's maps with the ones the world saved, region by region. A world
generator started again while the game runs (`/wgen regen`) makes a new set and puts it in place whole.

The landform, forest and shrub maps the sampler keeps per region are bounded too (512, 1,024 and 1,024 regions, least
recently used first), where they used to grow with every region ever sampled until the world closed.

## Caches and identity

Each tile is made once and kept in memory (64 MiB by default) and on disk (1 GiB for every world together), under
`ModData/algernonsterrainsampler/worlds/<savegame>/<identity>/`. The identity is a SHA-256 of everything that decides
what the sampler answers: the sampler's version and an algorithm revision, the game's version, the seed, the map and
region sizes, the sea level, the whole world configuration, the patched `worldgen/landforms.json`,
`worldgen/rockstrata.json` and `worldgen/geologicprovinces.json`, the default rock, every public setting of
`TerraGenConfig`, the Watersheds handoff, an admin revision number, and a canary of 64 columns sampled at start, their
top rock included, and which map generators the sampler follows (above). Anything that would change a tile changes
the identity, and a new identity is a new folder, so a stale tile is never served. Older folders, and every other world's, count against the quota until they are evicted,
least recently used first, and a folder unused for 90 days, with no write under way in it, is deleted when the server
starts (a client's own, when it next joins a server).

The memory cache is the service's one budget: tiles made for a client that has no room for them yet wait in it, and if
it needs the room they leave it and are read back from disk when the client is ready, made again only if the disk has
dropped them too or there is no disk cache. Beyond it, the server holds at most three tiles' bytes for each client.

Files are written to a temporary and moved into place, outside the lock reads take, so a cache hit never waits behind a
write; one that fails its checksum is deleted and sampled again; if the storage itself fails (at start, or five writes
in a row, a deleted folder being made again first; its own folder cannot be made or listed at start), the cache turns
itself off and the service carries on from memory. A file it cannot read or delete at start is skipped, and a temporary
younger than an hour is left alone, since another process on the same data folder may be writing it.

**A failure is never kept.** The tile service samples through `SampleColumnOrThrow`, which shares `SampleColumn`'s code but
throws where `SampleColumn` falls back to sea level. A failed tile is refused, with the reason, and sampled again when it
is next asked for. A failure's stack trace is logged the first time and counted after that, with a line a minute summing
up the repeats. Eight failed tiles in a row make the service check the sampler itself, on one column where a tile was
last made. If that fails too, the sampler is broken: the service refuses everything, tells every client through the
status, and checks that column again once a minute; the first check that passes, or the first tile made, brings it
back. If the column works, those tiles failed on their own, in an area that cannot be sampled, and only they are
refused. On a world type the sampler does not know (superflat), the service says so and does not start.

## Scheduling

- Persistent worker threads, so the sampler's per-thread caches stay warm between tiles: one on a dedicated server, cores
  less four (one to four) otherwise. Below normal priority where the platform allows, and slower when the game process
  passes 75 % CPU or a tick runs over 100 ms.
- One job per tile, however many clients want it.
- Each owner's most urgent tile first, round robin between owners, so one player cannot starve the rest.
- Cache hits are answered from memory, or from the disk cache's own thread, as soon as the owner has room for them, and
  never wait behind a tile being made.
- With a disk cache, tiles are made ahead of what a client has room for, onto disk; without one, only as it has room.
- A tile whose owners all left is finished and kept: it may be wanted again.
- With Watersheds, sampling stays on the main thread, a few milliseconds a tick, because nobody has checked whether
  Watersheds is thread safe. Encoding and writing still run on a worker.

## The channel

Channel `algernonsterrainsampler`, protobuf messages, protocol 1. A tile's key names a request.

| Message | Direction | Carries |
| --- | --- | --- |
| `TerrainTileHello` | client to server | Its protocol |
| `TerrainTileStatus` | server to client | Available or why not, the identity, the limits; sent again to every client whenever the service goes off or comes back |
| `TerrainTileWant` | client to server | Tiles to add, each with a priority, and tiles to drop; at most 4,096 keys a message |
| `TerrainTileProgress` | server to client | Every five seconds while tiles wait: wanted, on their way, being made, waiting |
| `TerrainTileData` | server to client | One fragment of a tile, at most 32 KiB |
| `TerrainTileReceived` | client to server | That a tile has arrived whole, kept or not |
| `TerrainTileRefused` | server to client | Tiles it will not send, many to a message, and the one reason for them all |

The server keeps each client's wanted set while it is connected, so a want message changes it; asking again for a tile
already wanted or on its way changes only its priority. Tile data and refusals go out under a byte budget per tick, all
clients together, round robin, refusals gathered by reason. Each client gets one tile finished before the next is
started, and no more than two it has not acknowledged, so a slow client is never buried and its other packets wait
behind two tiles at most. The server queues at most two more tiles for a client; the rest wait in the service, in its
memory cache or on disk, until the client has room, so a client that takes nothing costs the server a few hundred KiB.
A tile made and still waiting counts against `MaxWantedTiles`. A client asks the server for at most that many at once
and holds the rest itself, most urgent first, and assembles only tiles it asked for.

The game compresses every packet over 1,460 bytes again, tiles included, and the channel has no way to ask it not to. A
client that acknowledges tiles it has not read can still have the game buffer sends to it at up to the byte budget's
rate; the game offers no limit and no way to see it.

A client keeps its own disk cache of tiles (512 MiB by default) under the server's identity, in
`ModData/algernonsterrainsampler/remote/`, so a reconnect costs the server nothing. It gathers the requests of every
mod on it, so when two mods want one tile it crosses the network once. In single player it uses the server's cache,
which is on the same machine.

## Settings

`ModConfig/algernonsterrainsampler.json`, written by the server with its defaults:

| Setting | Default | What it bounds |
| --- | --- | --- |
| `NetworkSampling` | `true` | Whether clients may ask at all. Mods on the server always may |
| `RequiredPrivilege` | `""` | The privilege a player needs. Empty lets everyone; `controlserver` only administrators |
| `MaxWantedTiles` | 1,024 | Tiles one client may want at once, counting those made and waiting to be sent. Mods on the server share one such allowance |
| `MaxDistancePerStep` | 8,192 | A tile at step *s* is only served within 8,192 x *s* blocks of the asking player (16 km at step 2, 32 km at step 4), so nobody can ask for fine tiles across the whole world |
| `MaxDistance` | 32,768 | No tile whose centre is further than this from the asking player is served, at any step (a tile reaches up to half its diagonal past it, about 5.8 km at step 64), on top of `MaxDistancePerStep`, so nobody can pull most of a large world. Mods on the server are not limited |
| `Workers` | 0 | Sampling threads; 0 picks as above |
| `DiskCacheMegabytes` | 1,024 | The server's disk cache, all worlds together; 0 turns it off |
| `MemoryCacheMegabytes` | 64 | Encoded tiles kept in memory, those waiting for slow clients included: the server's one memory budget for tiles |
| `TransferKilobytesPerTick` | 256 | Tile data sent per tick (20 a second), all clients together |
| `CacheRevision` | 0 | Bump it to discard every cache, for a change the identity cannot see |
| `ClientDiskCacheMegabytes` | 512 | A client's own cache of tiles from multiplayer servers |
| `LogTileStats` | `false` | A line in the server log every minute while tiles are being made or sent |

Every check is made when a request arrives and again while it waits: a player who loses the privilege, or moves away,
loses the tiles they can no longer have.

**What a server shares.** With the defaults, any player whose client asks can have the heights, climate and top rock of
land they have not explored, out to 32 km. An operator who would rather not can turn `NetworkSampling` off, require a
privilege, or lower `MaxDistance`.

## Using it from another mod

`TerrainTileSystem.ServerInstance` on the server, `TerrainTileSystem.ClientInstance` on a client. Wait for `Available`,
or for `AvailabilityChanged`, then ask:

```csharp
TerrainTileSystem tiles = TerrainTileSystem.ClientInstance;
if (tiles?.Available == true)
{
    int step = 4;
    long handle = tiles.RequestTile(step, TerrainTileSystem.TileIndexOf(step, x), TerrainTileSystem.TileIndexOf(step, z), priority: 0,
        tile => Build(tile),          // on the main thread
        reason => Log(reason));       // instead, with why
    // later, if it is no longer wanted: tiles.CancelTile(handle);
}
```

| Member | |
| --- | --- |
| `static ServerInstance`, `static ClientInstance` | The service on each side, or null |
| `static int TileCells`, `static int TileSamples` | 128 and 129 |
| `static int TileIndexOf(int step, int world)` | The tile index holding a world coordinate, on either axis |
| `bool Available`, `string UnavailableReason` | Whether tiles can be asked for, and why not |
| `string Identity` | The cache identity: the same identity, the same tiles |
| `event Action AvailabilityChanged` | On the main thread |
| `long RequestTile(int step, int tileX, int tileZ, int priority, Action<TerrainTile> ready, Action<string> refused)` | Lower priorities first. Exactly one callback runs, on the main thread, never before this returns, unless cancelled first. Asked before the service is available, it is refused, with why, in a later tick |
| `void CancelTile(long handle)` | Neither callback will run |

Tiles are read only and shared between everyone who asked for them.

**Without a compile-time reference.** The signatures use only .NET's own types and `TerrainTile`, so a mod can bind them by
reflection, as Fast Map and VSPT reach the sampler today: find `AlgernonsTerrainSampler.Tiles.TerrainTileSystem`, read
`ClientInstance`, and invoke `RequestTile` with an `Action<object>` where `Action<TerrainTile>` is expected (delegate
variance allows it), reading the tile's public fields through cached `FieldInfo`s.

## Commands

| Command | Side | |
| --- | --- | --- |
| `/terrainsampler tiles` | Server, `controlserver` | The service's state: tiles made, hits, the queue, memory, disk, bytes sent |
| `/terrainsampler tilefetch [step] [radius]` | Server, `controlserver` | Makes the square of tiles round you, or spawn, as a server mod would ask for them, and reports how long they took; the radius is at most the widest square that fits `MaxWantedTiles`, 15 by default |
| `.terrainsampler tiles` | Client | Whether the server serves tiles, and what this client has had |
| `.terrainsampler fetch [step] [radius]` | Client | Asks for the square of tiles round you (radius at most 8) and reports how long they took and where they came from |
| `/terrainsampler rockcheck [radius]` | Server, `controlserver` | The sampled top rock against the generated chunks round you (`TopRockIdMap`), radius in chunks, at most 8: at the chunks' own terrain height, which tests the strata alone, as sampled, and with the world's own saved maps |
| `/terrainsampler mapcheck [radius]` | Server, `controlserver` | The sampler's climate, upheaval, ocean, landform, forest and shrub maps against the world's own for the generated map regions round you, radius in regions, at most 4, and which generators it follows. Regions where the game forced a landform (story structures) differ there, as the sampler does not apply those |

## Compatibility

- `requiredOnClient` and `requiredOnServer` are both true, as in 1.3.0, so nothing about installing it changes. A
  server with it has every player who joins install the same version, which mods that depend on it on clients rely on,
  and so every player on a server with 2.0.0 can take tiles. A player's copy is turned off on a server without it, and
  so is a copy whose version differs from the server's.
- A client and server on different protocol versions say so in the status, and do nothing.
- `SampleColumn` and `GetBlockColumnHeight` answer as in 1.3.0, at the same cost; `TerrainColumnSample.TopRock` is new,
  filled by `SampleColumnWithTopRock` and in tiles.
- `TerrainSamplerGenTerra.LandformMapByRegion` is now a bounded `Mapping.RegionCache`, not a `ConcurrentDictionary`,
  and `TerrainGenerationLib.GetOrLoadLerpedLandformMap` takes it.
- Landforms are now loaded at every world start, not once per process: they depend on the world height and on mods
  patching the asset, so a second world in the same run used to sample with the first world's.

## Measured

With an earlier build of this branch, on a two-core cloud server (Linux, 1.22.7), one worker, through
`/terrainsampler tilefetch`, while another mod was rebuilding its own map cache on the main thread:

| | |
| --- | --- |
| Step 4, nine tiles round spawn | 2.7 s; 246 ms a tile to sample; 60 KiB a tile |
| Step 4, 81 tiles (4,608 blocks across) | 19.3 s; 227 ms a tile; 59 KiB a tile |
| Step 32, nine tiles (12,288 blocks across) | 5.4 s; 512 ms a tile, since every sample is in a new chunk; 98 KiB a tile |
| The same nine tiles again | 51 ms, all from memory, nothing sampled |
| The 81 again, after a restart | 235 ms, all from disk, nothing sampled; 90 tiles, 5.5 MiB indexed at start |
| The identity | Unchanged across three restarts |

## Not done yet

- Prewarming: making the coarse tiles round spawn and players while the server is idle.
- The mods with bridges of their own moving onto this.
- A build for 1.21, if that is wanted.
