"""
Convert one or more LiDAR LAZ tiles to mttam_lidar.bin so the C# demo
renders authentic data instead of the synthetic placeholder.

Multiple tiles are merged into a single point cloud, with coordinates
re-centered on the combined centroid so the chart camera math stays
well-conditioned and float32 precision holds.

WHERE TO GET LAZ
----------------
Most public Bay Area LiDAR comes from USGS 3DEP (public domain) or
NCALM/USGS collections served via OpenTopography (CC BY 4.0 -- cite the
source). Useful starting points:

  USGS 3DEP LidarExplorer  https://apps.nationalmap.gov/lidar-explorer/
  OpenTopography portal    https://portal.opentopography.org/

OpenTopography typically delivers a ZIP with multiple LAZ tiles covering
the area you selected. This script accepts any number of input tiles --
pass them all on the command line, or use a glob.

USAGE
-----
    pip install laspy lazrs numpy

    # Single tile:
    python prepare_data.py path/to/tile.laz

    # Multiple tiles (will merge):
    python prepare_data.py tile1.laz tile2.laz tile3.laz

    # Glob:
    python prepare_data.py "tiles/*.laz"

    # Optional flags:
    #   --max-points N    Subsample to at most N points (default 2_500_000).
    #   --classes 2,6     Keep only these LAS classifications (e.g. 2=ground,
    #                     6=building). Omit to keep all.
    #   --out FILE        Output binary path (default: mttam_lidar.bin)

The output file is the same format the synthetic generator and C# loader use:
    int32   nPoints
    float32 * nPoints   X (UTM East,  centered on combined centroid, meters)
    float32 * nPoints   Y (UTM North, centered on combined centroid, meters)
    float32 * nPoints   Z (elevation, meters)
"""
from __future__ import annotations

import argparse
import glob
import struct
import sys
from pathlib import Path

import numpy as np

try:
    import laspy
except ImportError:
    sys.exit(
        "laspy is required.  Install with:\n"
        "    pip install laspy lazrs"
    )


def expand_inputs(specs):
    """Expand any glob patterns and return concrete files."""
    out = []
    for s in specs:
        matches = sorted(glob.glob(s))
        if matches:
            out.extend(Path(m) for m in matches)
        else:
            p = Path(s)
            if p.exists():
                out.append(p)
            else:
                sys.exit(f"Input not found: {s}")
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("laz", nargs="+",
                   help="Input .laz/.las file(s) or glob pattern. Multiple "
                        "files will be merged.")
    p.add_argument("--max-points", type=int, default=2_500_000,
                   help="Random subsample if combined cloud exceeds this "
                        "(default: 2,500,000)")
    p.add_argument("--classes", type=str, default=None,
                   help="Comma-separated LAS classification codes to keep "
                        "(e.g. '2,6' for ground+building). Omit to keep all.")
    p.add_argument("--out", type=Path,
                   default=Path(__file__).parent / "mttam_lidar.bin",
                   help="Output binary path (default: mttam_lidar.bin)")
    args = p.parse_args()

    inputs = expand_inputs(args.laz)
    if not inputs:
        sys.exit("No input files matched.")

    print(f"Reading {len(inputs)} file(s)...")
    Xs, Ys, Zs, Cs = [], [], [], []
    for f in inputs:
        with laspy.open(str(f)) as r:
            las = r.read()
        n = len(las.points)
        Xs.append(np.asarray(las.x, dtype=np.float64))
        Ys.append(np.asarray(las.y, dtype=np.float64))
        Zs.append(np.asarray(las.z, dtype=np.float64))
        Cs.append(np.asarray(las.classification))
        print(f"  {f.name}: {n:,} points  "
              f"X={Xs[-1].min():.0f}..{Xs[-1].max():.0f}  "
              f"Y={Ys[-1].min():.0f}..{Ys[-1].max():.0f}  "
              f"Z={Zs[-1].min():.0f}..{Zs[-1].max():.0f}")

    X = np.concatenate(Xs)
    Y = np.concatenate(Ys)
    Z = np.concatenate(Zs)
    cls = np.concatenate(Cs)
    print(f"\nMerged total: {len(X):,} points")

    # Optional classification filter.
    if args.classes:
        wanted = {int(c) for c in args.classes.split(",")}
        keep = np.isin(cls, list(wanted))
        X, Y, Z, cls = X[keep], Y[keep], Z[keep], cls[keep]
        print(f"After class filter {sorted(wanted)}: {len(X):,} points")

    # Random subsample if over budget.
    n = len(X)
    if n > args.max_points:
        rng = np.random.default_rng(seed=0)
        idx = rng.choice(n, size=args.max_points, replace=False)
        idx.sort()  # cache locality
        X, Y, Z, cls = X[idx], Y[idx], Z[idx], cls[idx]
        print(f"Subsampled to {len(X):,} points")

    n = len(X)
    if n == 0:
        sys.exit("No points left after filtering. Adjust --classes or use "
                 "different tiles.")

    # Center XY on combined centroid; Z stays in absolute elevation.
    cx, cy = float(X.mean()), float(Y.mean())
    X32 = (X - cx).astype(np.float32)
    Y32 = (Y - cy).astype(np.float32)
    Z32 = Z.astype(np.float32)

    print(f"\nCombined centroid (UTM): ({cx:.1f}, {cy:.1f}) - subtracted on output")
    print(f"  X range:   {X32.min():+.1f} .. {X32.max():+.1f}  m  "
          f"({X32.max() - X32.min():.0f} m wide)")
    print(f"  Y range:   {Y32.min():+.1f} .. {Y32.max():+.1f}  m  "
          f"({Y32.max() - Y32.min():.0f} m deep)")
    print(f"  Z range:   {Z32.min():+.1f} .. {Z32.max():+.1f}  m elevation")

    print(f"\nWriting {args.out} ...")
    with open(args.out, "wb") as f:
        f.write(struct.pack("<i", n))
        f.write(X32.tobytes())
        f.write(Y32.tobytes())
        f.write(Z32.tobytes())

    size_mb = args.out.stat().st_size / (1024 * 1024)
    print(f"Wrote {n:,} points -- {size_mb:.1f} MB")
    print()
    print("Rebuild the C# project so the new binary copies to bin\\... output dir.")


if __name__ == "__main__":
    main()
