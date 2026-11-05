# dmoprobe — ground-truth Sauerbraten .dmo demo parser

Small C++ driver that parses original Cube 2: Sauerbraten (SVN r6889) `.dmo` demo
files directly according to the engine protocol (`src/fpsgame/client.cpp` and
`src/fpsgame/server.cpp`).

Used offline by `tools/botlab` and automated unit tests to verify and assert
fidelity of the C# `DmoReader` and trajectory generation.

## Build

```sh
./tools/dmoprobe/build.sh
```

Builds `./tools/dmoprobe/dmoprobe` with `c++ -std=c++17 -O2 -lz`.

## Usage

```sh
# Human-readable summary
./tools/dmoprobe/dmoprobe <demo.dmo>

# Structured JSON (for automated comparison & Python ingestion)
./tools/dmoprobe/dmoprobe <demo.dmo> --json

# Customize sample positions per player
./tools/dmoprobe/dmoprobe <demo.dmo> --sample-limit 10
```

## Protocol reference

- Demo files are gzip streams (`zlib`).
- Header: `demoheader { char magic[16]="SAUERBRATEN_DEMO"; int version=1, protocol=260; }`.
- Records: `int gamemillis, int chan, int len` (little-endian), followed by `len` packet bytes.
- Channel 0: client positions (`N_POS`, fixed-point `DMF=16.0f`, velocity `DVELF=1.0f`), teleports (`N_TELEPORT`), jump pads (`N_JUMPPAD`).
- Channel 1: game messages (`N_MAPCHANGE`, `N_INITCLIENT`, `N_SPAWNSTATE`, `N_SPAWN`, `N_SHOTFX`, `N_DAMAGE`, `N_DIED`, `N_CLIENT`, etc.).
