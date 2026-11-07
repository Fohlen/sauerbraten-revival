# predictprobe — Sauerbraten Smoothmove / PredictPlayer Ground Truth

Small focused probe extracting the original Sauerbraten SVN r6889 movement smoothing and dead reckoning prediction logic from:
- `src/fpsgame/client.cpp:1189-1207` (packet reception, residual calculation with shortest-arc yaw wrapping, and thresholding)
- `src/fpsgame/fps.cpp:169-193` (dead reckoning with linear decay over `smoothmove = 75` ms)

## Building & Running

```sh
./build.sh
```

Dumps ground-truth JSON evaluations across all canonical scenarios (initial spawn, shortest-arc yaw wrap across 0°/360°, pitch transition, smoothmove decay over time, and teleport threshold exceeding `smoothdist = 32`).
