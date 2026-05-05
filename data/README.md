# LiDAR data

The C# app loads `mttam_lidar.bin` at startup. The repo ships with a
2.5M-point Mt. Tamalpais dataset already prepared (~29 MB). This folder
also contains the prep script for swapping in a different scene.

## Shipped dataset — Mt. Tamalpais

`mttam_lidar.bin` was prepared from three LAZ tiles of the NCALM 2006
Marin Headlands airborne LiDAR collection, distributed via
[OpenTopography](https://portal.opentopography.org/).

- 22.7M raw returns subsampled to 2.5M
- 2.8 km × 4.4 km coverage
- 0.7 m to 674 m elevation range
- License: CC BY 4.0 — cite OpenTopography and NCALM/NSF EAR

## Replacing the data

The same prep script handles any LAZ tile from any source. Useful starting
points:

- USGS 3DEP LidarExplorer: https://apps.nationalmap.gov/lidar-explorer/
  (public domain, no attribution required)
- OpenTopography portal: https://portal.opentopography.org/
  (CC BY 4.0, citation required — credit OpenTopography + the original collector)

Once you have one or more LAZ files:

    pip install laspy lazrs numpy
    python prepare_data.py path/to/tile1.laz [more_tiles.laz...]

Multiple tiles are merged automatically. The script subsamples to 2.5M
points by default. See `prepare_data.py --help` for filtering by LAS
classification, custom output path, etc.

## Binary format

`mttam_lidar.bin` is a flat little-endian layout the C# loader reads in
three `Buffer.BlockCopy` calls:

| Offset | Size              | Field                    |
|--------|-------------------|--------------------------|
| 0      | 4 bytes (int32)   | `nPoints`                |
| 4      | `4*nPoints` bytes | float32 X (meters, east) |
| ...    | `4*nPoints` bytes | float32 Y (meters, north)|
| ...    | `4*nPoints` bytes | float32 Z (meters, up)   |

Coordinates are pre-centered (centroid subtracted). The C# loader applies
the LiDAR-to-Pe3do axis swap (LiDAR Z → Pe3do Y).
