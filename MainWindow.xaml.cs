using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;

namespace MtTamalpaisLidar3D
{
    /// <summary>
    /// ProEssentials WPF 3D LiDAR Scatter — ComputeShader + per-point PointColors
    ///
    /// Renders a real airborne LiDAR scan of Mt. Tamalpais (Marin County, CA)
    /// as a Pe3do scatter chart with every point individually colored by
    /// elevation. The dataset ships from OpenTopography's mirror of the NCALM
    /// 2006 Marin Headlands collection, ~2.5M ground returns covering 2.8 km ×
    /// 4.4 km of terrain with 674 m of vertical relief from sea level to the
    /// ridge.
    ///
    /// Demonstrates the v10.0.0.24 ComputeShader path for PolyMode = Scatter,
    /// where 2000+ GPU cores construct the entire point cloud scene in parallel
    /// rather than the CPU walking each point sequentially.
    ///
    /// Scope: this is the "static cloud" entry point in the LiDAR repo series.
    /// No real-time append, no surface reconstruction — just one big scatter
    /// rendered as fast as possible. Future repos add the surface, contour, and
    /// composite views on the same dataset.
    ///
    /// Performance highlights:
    /// - 2.5M LiDAR returns rendered as 3D scatter points
    /// - ComputeShader = true: GPU-side vertex construction (v10.0.0.24)
    /// - PointColors per data point: every point gets an elevation-mapped color
    /// - Single-subset organization: 1 subset × N points (the natural shape of an
    ///   unstructured LiDAR cloud — no synthetic "rows" required)
    ///
    /// Data file:
    ///   mttam_lidar.bin — simple binary float layout produced by
    ///   data/prepare_data.py from one or more LAZ tiles. Format:
    ///     int32   nPoints
    ///     float32 * nPoints   X coords (UTM East, centered, meters)
    ///     float32 * nPoints   Y coords (UTM North, centered, meters)
    ///     float32 * nPoints   Z coords (elevation, meters)
    ///
    /// LiDAR-to-Pe3do coordinate convention:
    ///   Pe3do uses Y as the vertical "value" axis. LiDAR convention puts Z up.
    ///   The loader swaps:   LiDAR X --> Pe3do.X (east-west)
    ///                       LiDAR Y --> Pe3do.Z (north-south, depth)
    ///                       LiDAR Z --> Pe3do.Y (elevation, vertical)
    ///
    /// Controls:
    ///   Left drag       — rotate
    ///   Shift + drag    — pan
    ///   Mouse wheel     — zoom
    ///   Double-click    — start/stop auto-rotation
    ///   Right-click     — context menu (color schemes, view modes, export)
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // -------------------------------------------------------------------
        // Pe3do1_Loaded — chart initialization
        // -------------------------------------------------------------------
        void Pe3do1_Loaded(object sender, RoutedEventArgs e)
        {
            // ===================================================================
            // Step 1 — Load LiDAR binary
            //
            // Simple flat binary: 4-byte point count then three blocks of
            // float32 X, Y, Z. We read whole arrays at once — much faster than
            // BinaryReader.ReadSingle() in a loop for ~1M points.
            // ===================================================================
            string filepath = AppDomain.CurrentDomain.BaseDirectory + "mttam_lidar.bin";

            if (!File.Exists(filepath))
            {
                MessageBox.Show(
                    "mttam_lidar.bin not found.\n\n" +
                    "Build the dataset by running:\n" +
                    "    python data\\prepare_data.py path\\to\\*.laz\n\n" +
                    "See data/README.md for where to download LAZ tiles.\n\n" +
                    "Then rebuild so the file copies to the output directory.",
                    "Data file missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                Application.Current.Shutdown();
                return;
            }

            int nPoints;
            float[] lidarX, lidarY, lidarZ;
            using (var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                nPoints = br.ReadInt32();
                int byteCount = nPoints * sizeof(float);

                lidarX = new float[nPoints];
                lidarY = new float[nPoints];
                lidarZ = new float[nPoints];
                Buffer.BlockCopy(br.ReadBytes(byteCount), 0, lidarX, 0, byteCount);
                Buffer.BlockCopy(br.ReadBytes(byteCount), 0, lidarY, 0, byteCount);
                Buffer.BlockCopy(br.ReadBytes(byteCount), 0, lidarZ, 0, byteCount);
            }

            // Compute elevation range for the colormap. We'll color every point
            // by its Z value, so we need the data extent up front.
            float zMin = float.MaxValue, zMax = float.MinValue;
            for (int i = 0; i < nPoints; i++)
            {
                if (lidarZ[i] < zMin) zMin = lidarZ[i];
                if (lidarZ[i] > zMax) zMax = lidarZ[i];
            }

            // ===================================================================
            // Step 2 — Build Pe3do XYZ arrays with the coordinate swap
            //
            // Pe3do uses Y as the up axis. LiDAR uses Z as up. We allocate three
            // float arrays in Pe3do's convention and swap on the fly. A single
            // pass is enough — the cost is dominated by the FastCopyFrom calls
            // below, not this loop.
            // ===================================================================
            float[] peX = new float[nPoints];
            float[] peY = new float[nPoints];
            float[] peZ = new float[nPoints];
            for (int i = 0; i < nPoints; i++)
            {
                peX[i] = lidarX[i];   // east-west
                peY[i] = lidarZ[i];   // elevation goes into Pe3do's vertical axis
                peZ[i] = lidarY[i];   // north-south (depth into chart)
            }

            // ===================================================================
            // Step 3 — Build per-point colors (LiDAR's signature visual)
            //
            // Every point gets its own color sampled from a turbo-style colormap
            // keyed off elevation. PointColors[subset, point] is the per-data-point
            // color array — when populated it overrides SubsetColors for the
            // rendered points.
            //
            // We build int[] directly and use FastCopyFrom(int[]) — the canonical
            // documented bulk path. Packing follows the peColor32 struct layout
            // verified from the metadata: { byte R, byte G, byte B, byte A } in
            // declaration order. Reinterpreted as a little-endian int that's:
            //
            //     int packed = (A << 24) | (B << 16) | (G << 8) | R;
            //
            // (Note: this is NOT what System.Drawing.Color.ToArgb() returns —
            // ToArgb produces 0xAARRGGBB, while peColor32-as-int is 0xAABBGGRR.
            // R and B are swapped.)
            //
            // We deliberately avoid the ColorArrayToPeColor32 helper here — its
            // call form (static-on-type vs instance-on-property) varies in ways
            // that broke a v10.0.0.24 build. FastCopyFrom(int[]) is rock-solid
            // across versions.
            // ===================================================================
            int[] packedColors = new int[nPoints];
            float zRange = Math.Max(1f, zMax - zMin);
            for (int i = 0; i < nPoints; i++)
            {
                float t = (lidarZ[i] - zMin) / zRange;            // [0, 1]
                var (r, g, b) = ElevationColorBytes(t);
                packedColors[i] = (255 << 24) | (b << 16) | (g << 8) | r;
            }

            // ===================================================================
            // Step 4 — Configure Pe3do
            //
            // RenderEngine MUST be set before QuickStyle for proper Direct3D init.
            // ===================================================================
            Pe3do1.PeFunction.Reset();
            Pe3do1.PeConfigure.RenderEngine = RenderEngine.Direct3D;

            // -------------------------------------------------------------------
            // Scatter mode (PolyMode + Method)
            //
            // PolyMode.Scatter selects the 3D scatter category, then Method=Zero
            // selects the "points only" variant (1=lines, 2=points+lines,
            // 3=area/waterfall). With ComputeShader=true these scatter modes are
            // GPU-constructed in v10.0.0.24.
            // -------------------------------------------------------------------
            Pe3do1.PePlot.PolyMode = PolyMode.Scatter;
            Pe3do1.PePlot.Method = ThreeDGraphPlottingMethod.Zero;   // 0 = Points

            // Single-subset organization: one point cloud, N points.
            // PointColors is then [1 x N] — one color per LiDAR return.
            Pe3do1.PeData.Subsets = 1;
            Pe3do1.PeData.Points = nPoints;

            // Pre-size the internal arrays before FastCopyFrom (recommended pattern
            // for large datasets — touch the last cell to allocate the full block).
            Pe3do1.PeData.X[0, nPoints - 1] = 0f;
            Pe3do1.PeData.Y[0, nPoints - 1] = 0f;
            Pe3do1.PeData.Z[0, nPoints - 1] = 0f;

            // Bulk-load the coordinate arrays.
            Pe3do1.PeData.X.FastCopyFrom(peX, nPoints);
            Pe3do1.PeData.Y.FastCopyFrom(peY, nPoints);
            Pe3do1.PeData.Z.FastCopyFrom(peZ, nPoints);

            // Bulk-load per-point colors via the int[] overload (peColor32 layout).
            // With one subset and SubsetForPointColors unset, all points pick
            // their color from this array.
            Pe3do1.PePlot.PointColors.FastCopyFrom(packedColors);

            // SubsetColors still drives the legend swatch even when PointColors is
            // active. Set a sensible mid-elevation color for the lone subset.
            {
                var (r, g, b) = ElevationColorBytes(0.5f);
                Pe3do1.PeColor.SubsetColors[0] = Color.FromArgb(255, r, g, b);
            }

            // -------------------------------------------------------------------
            // Point appearance — simple solid dots
            //
            // Per the v10.0.0.24 release notes, ComputeShader for 3D scatter
            // points is bounded at 8 vertices / 24 indices per symbol. DotSolid
            // is the simplest symbol and the right choice for million-point clouds.
            // -------------------------------------------------------------------
            Pe3do1.PePlot.SubsetPointTypes[0] = PointType.DotSolid;
            Pe3do1.PePlot.PointSize = PointSize.Small;

            // ===================================================================
            // Step 5 — ComputeShader (the v10.0.0.24 scatter path)
            //
            // ComputeShader=true delegates per-point vertex construction to the
            // GPU. With ~1M points, CPU-side construction would walk each point
            // sequentially on a single core. GPU-side runs the same work across
            // 2000+ shader cores in parallel — the difference is dramatic on
            // dense scatter clouds.
            //
            // StagingBufferX/Y/Z route data to GPU via intermediate staging
            // memory, avoiding pipeline stalls during the upload.
            //
            // Comment the four lines below to compare CPU-side construction.
            // ===================================================================
            Pe3do1.PeData.ComputeShader = true;
            Pe3do1.PeData.StagingBufferX = true;
            Pe3do1.PeData.StagingBufferY = true;
            Pe3do1.PeData.StagingBufferZ = true;

            // ===================================================================
            // Step 6 — Manual scales (skip per-point ranging)
            //
            // For static scatter, computing min/max once at load time is much
            // cheaper than letting the engine scan ~1M points. Manual scale
            // bounds are derived from the data extent with a small pad.
            // ===================================================================
            float padXZ = 50f;   // meters of horizontal padding
            float padY  = 30f;   // meters of vertical padding

            float xMin = Min(peX), xMax = Max(peX);
            float zMinChart = Min(peZ), zMaxChart = Max(peZ);

            Pe3do1.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;
            Pe3do1.PeGrid.Configure.ManualMinX = xMin - padXZ;
            Pe3do1.PeGrid.Configure.ManualMaxX = xMax + padXZ;

            Pe3do1.PeGrid.Configure.ManualScaleControlY = ManualScaleControl.MinMax;
            Pe3do1.PeGrid.Configure.ManualMinY = zMin - 5f;            // elevation
            Pe3do1.PeGrid.Configure.ManualMaxY = zMax + padY;

            Pe3do1.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.MinMax;
            Pe3do1.PeGrid.Configure.ManualMinZ = zMinChart - padXZ;
            Pe3do1.PeGrid.Configure.ManualMaxZ = zMaxChart + padXZ;

            // SkipRanging tells the engine not to walk the data again to compute
            // min/max — saves a full pass over ~1M points on (re)init.
            Pe3do1.PeData.SkipRanging = true;

            // ===================================================================
            // Step 7 — Camera / view defaults
            //
            // Initial framing is a southwest 3/4 view: looking at the bridge axis
            // from the SF side, slightly elevated. DegreePrompting prints the
            // live camera params in the top-left so it's easy to dial in your
            // own preferred angle and copy the values back here.
            // ===================================================================
            // Hero camera defaults — captured from a hand-tuned view of the
            // Mt. Tam dataset using DegreePrompting=true (the corner readout
            // shows rotation, ViewingHeight, DxZoom, DxViewportX, DxViewportY,
            // and Light0 X/Y/Z, in that order). To find your own preferred
            // angle, drag the chart to taste, read the corner values, paste
            // them here, rebuild.
            Pe3do1.PeUserInterface.Scrollbar.ViewingHeight = 19;
            Pe3do1.PeUserInterface.Scrollbar.DegreeOfRotation = 200;
            Pe3do1.PePlot.Option.DxFOV = 1;          // normal perspective
            Pe3do1.PePlot.Option.DxZoom = 0.49F;
            Pe3do1.PePlot.Option.DxViewportX = 0.00F;
            Pe3do1.PePlot.Option.DxViewportY = 0.33F;
            Pe3do1.PePlot.Option.DxZoomMax = 20F;
            Pe3do1.PePlot.Option.DxZoomMin = -16F;
            Pe3do1.PePlot.Option.DxFitControlShape = false;
            Pe3do1.PePlot.Option.DxViewportPanFactor = 1.5F;

            Pe3do1.PeUserInterface.Scrollbar.ScrollSmoothness = 3;
            Pe3do1.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness = 4;
            Pe3do1.PeUserInterface.Scrollbar.PinchZoomSmoothness = 2;
            Pe3do1.PeUserInterface.Scrollbar.MouseWheelZoomFactor = 1.8F;
            Pe3do1.PeUserInterface.Scrollbar.MouseDraggingX = true;
            Pe3do1.PeUserInterface.Scrollbar.MouseDraggingY = true;

            Pe3do1.PePlot.Option.DegreePrompting = true;

            // Light position — angled to give some sense of depth even on points.
            // Light position — from DegreePrompting readout matching the hero
            // camera angle. Adjust by middle-mouse drag in the running app and
            // paste new values here.
            Pe3do1.PeFunction.SetLight(0, 4.06F, -5.42F, 8.13F);
            Pe3do1.PePlot.Option.LightStrength = 0.65F;
            Pe3do1.PePlot.Option.BackLight = 10;
            
            // ===================================================================
            // Step 8 — Visual styling
            // ===================================================================
            Pe3do1.PeColor.QuickStyle = QuickStyle.DarkNoBorder;
            Pe3do1.PeColor.BitmapGradientMode = false;

            Pe3do1.PeString.MainTitle =
                $"Mt. Tamalpais LiDAR — {nPoints:N0} points, GPU ComputeShader scatter";
            Pe3do1.PeString.SubTitle = "ProEssentials v10.0.0.24+ — comment the ComputeShader lines for the slow-path comparison";
            Pe3do1.PeString.XAxisLabel = "East (m)";
            Pe3do1.PeString.YAxisLabel = "Elevation (m)";
            Pe3do1.PeString.ZAxisLabel = "North (m)";

            Pe3do1.PeFont.Fixed = true;
            // Fully qualify: WPF's Control.FontSize (inherited by Window/MainWindow)
            // is a `double` instance property, and unqualified `FontSize` resolves
            // to it before the `using Gigasoft.ProEssentials.Enums;` enum type.
            Pe3do1.PeFont.FontSize = Gigasoft.ProEssentials.Enums.FontSize.Medium;
            Pe3do1.PeFont.Label.Bold = true;
            Pe3do1.PeConfigure.TextShadows = TextShadows.BoldText;

            // Axis aspect — these control how the three axes are stretched
            // relative to each other in the viewport, independent of data range.
            //
            // Our data is ~3700m wide × 3100m deep × 291m tall — extreme
            // anisotropic ratio. With all three left at 1.0 the engine
            // typically equalizes axes in viewport space, which means the
            // 291m elevation axis ends up the SAME visual length as the
            // 3700m horizontal axis — strong vertical exaggeration. The
            // bridge towers will read prominently this way.
            //
            // To dial it back toward geometrically-faithful proportions,
            // shrink GridAspectY:
            //     GridAspectY = 0.5F   ~2x vertical exaggeration
            //     GridAspectY = 0.3F   ~4x exaggeration (still readable)
            //     GridAspectY = 0.08F  approximately to-scale (towers tiny)
            //
            // Conversely, GridAspectX/Z > 1.0 stretches the horizontal plane
            // for a more panoramic feel (the realtime-surface example uses
            // GridAspectX = 10 for that reason).
            Pe3do1.PeGrid.Option.GridAspectX = 1.0F;
            Pe3do1.PeGrid.Option.GridAspectY = 1.0F;
            Pe3do1.PeGrid.Option.GridAspectZ = 1.0F;

            // Hide the standard subset legend — single subset, would just say "1".
            Pe3do1.PeLegend.Show = false;
            Pe3do1.PeUserInterface.Menu.LegendLocation = MenuControl.Show;

            // Render padding
            Pe3do1.PeConfigure.ImageAdjustLeft = 50;
            Pe3do1.PeConfigure.ImageAdjustRight = 50;
            Pe3do1.PeConfigure.ImageAdjustTop = 30;
            Pe3do1.PeConfigure.ImageAdjustBottom = 30;

            Pe3do1.PeConfigure.PrepareImages = true;
            Pe3do1.PeConfigure.CacheBmp = true;
            Pe3do1.PeConfigure.AntiAliasGraphics = true;
            Pe3do1.PeConfigure.AntiAliasText = true;

            Pe3do1.PeUserInterface.Allow.FocalRect = false;

            // Cursor / hotspot tracking — disabled for this dataset.
            //
            // PromptTracking does CPU-side octree hit-testing to find the
            // nearest data point under the cursor and pop a tooltip. The
            // octree itself is fast in absolute terms, but at 2.5M points
            // each mouse-move triggers a traversal that takes ~2 seconds
            // on this dataset — long enough that the lag becomes the demo's
            // main impression instead of the render. Ironic outcome:
            // turning ON a "live data tooltip" feature makes the chart feel
            // SLOWER than turning it off.
            //
            // Below ~500K points the tracking is responsive and very
            // useful. Uncomment the four lines if you reduce the point
            // count via prepare_data.py --max-points 500000 (or smaller).

            // Pe3do1.PeUserInterface.HotSpot.Data = true;
            // Pe3do1.PeUserInterface.Cursor.PromptTracking = true;
            // Pe3do1.PeUserInterface.Cursor.PromptStyle = CursorPromptStyle.XYZValues;
            // Pe3do1.PeUserInterface.Cursor.HighlightColor = Color.FromArgb(255, 255, 0, 0);

            // Export defaults
            Pe3do1.PeSpecial.DpiX = 600;
            Pe3do1.PeSpecial.DpiY = 600;
            Pe3do1.PeUserInterface.Dialog.ExportSizeDef  = ExportSizeDef.NoSizeOrPixel;
            Pe3do1.PeUserInterface.Dialog.ExportTypeDef  = ExportTypeDef.Png;
            Pe3do1.PeUserInterface.Dialog.ExportDestDef  = ExportDestDef.Clipboard;
            Pe3do1.PeUserInterface.Dialog.ExportUnitXDef = "1600";
            Pe3do1.PeUserInterface.Dialog.ExportUnitYDef = "900";
            Pe3do1.PeUserInterface.Dialog.ExportImageDpi = 300;
            Pe3do1.PeUserInterface.Dialog.AllowEmfExport = false;
            Pe3do1.PeUserInterface.Dialog.AllowWmfExport = false;

            // ===================================================================
            // Step 9 — Finalize
            //
            // Force3dxNewColors needed because we populated PointColors after
            // the chart was first reset.
            // Force3dxVerticeRebuild rebuilds the GPU vertex buffers.
            // ===================================================================
            Pe3do1.PeFunction.Force3dxNewColors = true;
            Pe3do1.PeFunction.Force3dxVerticeRebuild = true;
            Pe3do1.PeFunction.ReinitializeResetImage();
            Pe3do1.Invalidate();
            Pe3do1.UpdateLayout();
            Pe3do1.Refresh();
        }

        // -------------------------------------------------------------------
        // ElevationColorBytes — turbo-ish colormap with anchor stops
        //
        // Tuned for Mt. Tamalpais's terrain distribution (sea level → 674 m
        // ridgeline). Most of the data sits in mid-slope, so the green band
        // is widest. Coastal flats read teal/cyan, low foothills green,
        // upper slopes yellow, and the ridgeline burns through orange/red.
        //
        // Linear interpolation between anchor stops. Replace with viridis,
        // a classic LiDAR ramp, or a hypsometric tint by editing the stops.
        //
        // Returns raw bytes so the caller can pack them into the int[] format
        // used by FastCopyFrom, or build a WPF Color from them for SubsetColors
        // and similar.
        // -------------------------------------------------------------------
        private static readonly (float Stop, byte R, byte G, byte B)[] kStops =
        {
            (0.00f,  20,  60, 130),   // deep blue (lowest elevation, near sea level)
            (0.08f,   0, 130, 180),   // mid-blue (coastal flats)
            (0.18f,   0, 200, 190),   // cyan/teal (low coast)
            (0.32f,   0, 190,  90),   // green (foothills)
            (0.55f, 180, 210,  40),   // yellow-green (mid slopes)
            (0.75f, 240, 170,  40),   // orange (upper slopes)
            (1.00f, 230,  60,  60),   // red (ridge / Mt. Tam summit)
        };

        private static (byte R, byte G, byte B) ElevationColorBytes(float t)
        {
            if (t <= kStops[0].Stop) return (kStops[0].R, kStops[0].G, kStops[0].B);
            if (t >= kStops[^1].Stop) return (kStops[^1].R, kStops[^1].G, kStops[^1].B);

            for (int i = 1; i < kStops.Length; i++)
            {
                if (t <= kStops[i].Stop)
                {
                    var lo = kStops[i - 1];
                    var hi = kStops[i];
                    float u = (t - lo.Stop) / (hi.Stop - lo.Stop);
                    byte r = (byte)(lo.R + (hi.R - lo.R) * u);
                    byte g = (byte)(lo.G + (hi.G - lo.G) * u);
                    byte b = (byte)(lo.B + (hi.B - lo.B) * u);
                    return (r, g, b);
                }
            }
            return (255, 255, 255);   // unreachable
        }

        private static float Min(float[] a)
        {
            float m = a[0];
            for (int i = 1; i < a.Length; i++) if (a[i] < m) m = a[i];
            return m;
        }
        private static float Max(float[] a)
        {
            float m = a[0];
            for (int i = 1; i < a.Length; i++) if (a[i] > m) m = a[i];
            return m;
        }
    }
}
