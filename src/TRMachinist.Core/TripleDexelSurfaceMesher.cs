using System.Numerics;
using System.Collections.Concurrent;

namespace TRMachinist.Core;

public readonly record struct IpwChunkKey(int X, int Y, int Z);

public sealed record IpwSurfaceChunk(
    IpwChunkKey Key,
    IReadOnlyList<TaggedTriangleMeshData> Meshes)
{
    public int TriangleCount => Meshes.Sum(item => item.Mesh.TriangleCount);
}

public sealed partial class TripleDexelStock
{
    // Render partition only: no machine, program or workpiece dimensions are
    // embedded in the algorithm. Smaller chunks keep live cutter updates local.
    // 32 cells keeps a live 0.15 mm tile about 4.8 mm wide. Smaller regional
    // rebuilds reach the DirectX buffers sooner, so a cutter trail cannot keep
    // displaying an old 9.6 mm tile while the exact stock is already current.
    // The value is in cells, not part dimensions, and therefore remains
    // independent of the workpiece and machine.
    private const int SurfaceChunkCells = 32;
    private static readonly ParallelOptions SurfaceMeshingParallelOptions = new()
    {
        // Surface construction allocates output buffers. Using every logical
        // processor at once can create a large workstation-GC pause on high
        // core-count PCs, so keep enough CPU headroom for the UI/render thread.
        MaxDegreeOfParallelism = Math.Clamp(Math.Max(1, Environment.ProcessorCount / 4), 1, 8)
    };
    private readonly HashSet<IpwChunkKey> _dirtySurfaceChunks = new();
    private readonly Dictionary<IpwChunkKey, long> _dirtySurfacePriorities = new();
    private readonly HashSet<IpwChunkKey> _knownSurfaceChunks = new();
    private long _dirtySurfaceRevision;

    private static readonly (int A, int B)[] CellEdges =
    {
        (0, 1), (3, 2), (4, 5), (7, 6),
        (0, 3), (1, 2), (4, 7), (5, 6),
        (0, 4), (1, 5), (2, 6), (3, 7)
    };

    public int DirtySurfaceChunkCount => _dirtySurfaceChunks.Count;
    public int SurfaceChunkCount =>
        ChunkCount(_xCoordinates.Length + 1) *
        ChunkCount(_yCoordinates.Length + 1) *
        ChunkCount(_zCoordinates.Length + 1);

    /// <summary>
    /// Rebuilds only chunks touched since the previous call. Geometry is a
    /// surface-net cache over exact tri-dexel edge crossings; interval rays,
    /// not triangles, remain the authoritative stock representation.
    /// </summary>
    public IReadOnlyList<IpwSurfaceChunk> BuildDirtySurfaceChunks(
        ZDexelTarget? target = null,
        int maximumChunkCount = int.MaxValue)
    {
        if (_dirtySurfaceChunks.Count == 0 || maximumChunkCount <= 0)
            return Array.Empty<IpwSurfaceChunk>();
        var orderedKeys = _dirtySurfaceChunks
            .OrderByDescending(key => _dirtySurfacePriorities.GetValueOrDefault(key))
            .ThenBy(key => key.Z).ThenBy(key => key.Y).ThenBy(key => key.X)
            .ToArray();
        var requestedCount = Math.Min(maximumChunkCount, orderedKeys.Length);
        IpwChunkKey[] keys;
        if (requestedCount >= orderedKeys.Length)
        {
            keys = orderedKeys;
        }
        else
        {
            // Pure newest-first scheduling kept the cutter neighbourhood fresh
            // but could starve an older region indefinitely while a long tool
            // path continued marking new chunks.  That appeared as stock
            // remnants disappearing well after the cutter had passed.  Every
            // batch now reserves one third for the oldest outstanding regions
            // while retaining two thirds for the live cutter neighbourhood.
            var newestCount = Math.Max(1, (requestedCount * 2 + 2) / 3);
            var oldestCount = requestedCount - newestCount;
            keys = orderedKeys.Take(newestCount)
                .Concat(orderedKeys.Skip(orderedKeys.Length - oldestCount).Reverse())
                .ToArray();
        }

        if (keys.Length == 1)
        {
            var chunk = BuildSurfaceChunk(keys[0], target);
            TrackSurfaceChunk(chunk);
            _dirtySurfaceChunks.Remove(keys[0]);
            _dirtySurfacePriorities.Remove(keys[0]);
            return new[] { chunk };
        }

        // Dirty chunks only read the committed dexel fields and build entirely
        // independent render buffers. Preserve deterministic key order while
        // letting separate regions use separate CPU cores.
        var chunks = new IpwSurfaceChunk[keys.Length];
        Parallel.For(0, keys.Length, SurfaceMeshingParallelOptions, index =>
            chunks[index] = BuildSurfaceChunk(keys[index], target));
        foreach (var chunk in chunks)
        {
            TrackSurfaceChunk(chunk);
            _dirtySurfaceChunks.Remove(chunk.Key);
            _dirtySurfacePriorities.Remove(chunk.Key);
        }
        return chunks;
    }

    public IReadOnlyList<IpwSurfaceChunk> BuildAllSurfaceChunks(ZDexelTarget? target = null)
    {
        MarkAllSurfaceChunksDirty();
        return BuildDirtySurfaceChunks(target);
    }

    private void MarkAllSurfaceChunksDirty()
    {
        if (!_surfaceCacheEnabled)
        {
            _dirtySurfaceChunks.Clear();
            _dirtySurfacePriorities.Clear();
            _knownSurfaceChunks.Clear();
            return;
        }
        var pending = new HashSet<IpwChunkKey>(_dirtySurfaceChunks);
        pending.UnionWith(_knownSurfaceChunks);
        DiscoverSurfaceChunkKeys(pending);
        _dirtySurfaceChunks.Clear();
        _dirtySurfacePriorities.Clear();
        // Previously rendered chunks must also be rebuilt so reset/full rebuild
        // can remove a cavity that no longer exists.
        var revision = ++_dirtySurfaceRevision;
        foreach (var key in pending)
        {
            _dirtySurfaceChunks.Add(key);
            _dirtySurfacePriorities[key] = revision;
        }
    }

    private void MarkSurfaceChunksDirty(Bounds3 changedBounds)
    {
        if (!_surfaceCacheEnabled) return;
        var padding = (float)Math.Max(XPitch, Math.Max(YPitch, ZPitch)) * 2;
        var minimum = changedBounds.Min - new Vector3(padding);
        var maximum = changedBounds.Max + new Vector3(padding);
        var firstX = DirtyCellMinimum(_xCoordinates, minimum.X);
        var firstY = DirtyCellMinimum(_yCoordinates, minimum.Y);
        var firstZ = DirtyCellMinimum(_zCoordinates, minimum.Z);
        var lastX = DirtyCellMaximum(_xCoordinates, maximum.X);
        var lastY = DirtyCellMaximum(_yCoordinates, maximum.Y);
        var lastZ = DirtyCellMaximum(_zCoordinates, maximum.Z);

        var revision = ++_dirtySurfaceRevision;
        for (var z = firstZ / SurfaceChunkCells; z <= lastZ / SurfaceChunkCells; z++)
        for (var y = firstY / SurfaceChunkCells; y <= lastY / SurfaceChunkCells; y++)
        for (var x = firstX / SurfaceChunkCells; x <= lastX / SurfaceChunkCells; x++)
        {
            var key = new IpwChunkKey(x, y, z);
            _dirtySurfaceChunks.Add(key);
            _dirtySurfacePriorities[key] = revision;
        }
    }

    private IpwSurfaceChunk BuildSurfaceChunk(IpwChunkKey key, ZDexelTarget? target)
    {
        var xCellCount = _xCoordinates.Length + 1;
        var yCellCount = _yCoordinates.Length + 1;
        var zCellCount = _zCoordinates.Length + 1;
        var xStart = key.X * SurfaceChunkCells;
        var yStart = key.Y * SurfaceChunkCells;
        var zStart = key.Z * SurfaceChunkCells;
        var xEnd = Math.Min(xCellCount, xStart + SurfaceChunkCells);
        var yEnd = Math.Min(yCellCount, yStart + SurfaceChunkCells);
        var zEnd = Math.Min(zCellCount, zStart + SurfaceChunkCells);

        // A uniform 0.15 mm lattice contains hundreds of millions of volume
        // cells, but only the cells next to dexel interval end points can own a
        // surface. Collect those boundary edges first instead of sweeping all
        // 32^3 cells in every render chunk.
        using var workspace = SurfaceChunkWorkspace.Rent(this, xStart, yStart, zStart);
        var surfaceEdges = CollectSurfaceEdges(workspace, xStart, xEnd, yStart, yEnd, zStart, zEnd);
        if (surfaceEdges.Count == 0)
            return new IpwSurfaceChunk(key, Array.Empty<TaggedTriangleMeshData>());

        // Quads owned by this chunk also use the immediately positive neighbour
        // cell. Building exactly the four cells around each boundary edge keeps
        // chunk seams closed without visiting interior volume cells.
        foreach (var edge in surfaceEdges)
        {
            switch (edge.Axis)
            {
                case SurfaceAxis.X:
                    workspace.RequireCell(edge.X, edge.Y, edge.Z);
                    workspace.RequireCell(edge.X, edge.Y + 1, edge.Z);
                    workspace.RequireCell(edge.X, edge.Y + 1, edge.Z + 1);
                    workspace.RequireCell(edge.X, edge.Y, edge.Z + 1);
                    break;
                case SurfaceAxis.Y:
                    workspace.RequireCell(edge.X, edge.Y, edge.Z);
                    workspace.RequireCell(edge.X, edge.Y, edge.Z + 1);
                    workspace.RequireCell(edge.X + 1, edge.Y, edge.Z + 1);
                    workspace.RequireCell(edge.X + 1, edge.Y, edge.Z);
                    break;
                default:
                    workspace.RequireCell(edge.X, edge.Y, edge.Z);
                    workspace.RequireCell(edge.X + 1, edge.Y, edge.Z);
                    workspace.RequireCell(edge.X + 1, edge.Y + 1, edge.Z);
                    workspace.RequireCell(edge.X, edge.Y + 1, edge.Z);
                    break;
            }
        }

        foreach (var cell in workspace.RequiredCells)
        {
            if (cell.X < 0 || cell.X >= xCellCount ||
                cell.Y < 0 || cell.Y >= yCellCount ||
                cell.Z < 0 || cell.Z >= zCellCount)
                continue;
            var vertex = BuildCellVertex(workspace, cell.X, cell.Y, cell.Z);
            if (vertex is not null) workspace.SetCellVertex(cell.X, cell.Y, cell.Z, vertex.Value);
        }

        var builders = workspace.Builders;
        var planarQuads = workspace.PlanarGroups;

        foreach (var edge in surfaceEdges
                     .OrderBy(edge => edge.Axis)
                     .ThenBy(edge => edge.Z)
                     .ThenBy(edge => edge.Y)
                     .ThenBy(edge => edge.X))
        {
            switch (edge.Axis)
            {
                case SurfaceAxis.X:
                    AddQuad(SurfaceAxis.X, edge.X, edge.Y, edge.Z,
                        Cell(edge.X, edge.Y, edge.Z),
                        Cell(edge.X, edge.Y + 1, edge.Z),
                        Cell(edge.X, edge.Y + 1, edge.Z + 1),
                        Cell(edge.X, edge.Y, edge.Z + 1),
                        edge.LowerOccupied, edge.Tag, target);
                    break;
                case SurfaceAxis.Y:
                    AddQuad(SurfaceAxis.Y, edge.Y, edge.Z, edge.X,
                        Cell(edge.X, edge.Y, edge.Z),
                        Cell(edge.X, edge.Y, edge.Z + 1),
                        Cell(edge.X + 1, edge.Y, edge.Z + 1),
                        Cell(edge.X + 1, edge.Y, edge.Z),
                        edge.LowerOccupied, edge.Tag, target);
                    break;
                default:
                    AddQuad(SurfaceAxis.Z, edge.Z, edge.X, edge.Y,
                        Cell(edge.X, edge.Y, edge.Z),
                        Cell(edge.X + 1, edge.Y, edge.Z),
                        Cell(edge.X + 1, edge.Y + 1, edge.Z),
                        Cell(edge.X, edge.Y + 1, edge.Z),
                        edge.LowerOccupied, edge.Tag, target);
                    break;
            }
        }

        EmitPlanarQuads();

        var meshes = builders
            .Where(pair => pair.Value.TriangleCount > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new TaggedTriangleMeshData(pair.Key, pair.Value.Build()))
            .ToArray();
        return new IpwSurfaceChunk(key, meshes);

        SurfaceVertex? Cell(int x, int y, int z) =>
            workspace.CellVertex(x, y, z);

        void AddQuad(
            SurfaceAxis axis,
            int layer,
            int u,
            int v,
            SurfaceVertex? a,
            SurfaceVertex? b,
            SurfaceVertex? c,
            SurfaceVertex? d,
            bool lowerEdgeOccupied,
            int edgeTag,
            ZDexelTarget? targetModel)
        {
            if (a is null || b is null || c is null || d is null) return;
            var qa = a.Value;
            var qb = b.Value;
            var qc = c.Value;
            var qd = d.Value;
            var fallbackTag = ResolveSurfaceTag(qa, qb, qc, qd);
            var tag = edgeTag > 0 ? edgeTag : fallbackTag;
            var positiveNormal = lowerEdgeOccupied;
            tag = ResolveTargetTag((qa.Position + qb.Position + qc.Position + qd.Position) * 0.25f, tag, targetModel);

            if (IsAxisPlanar(axis, qa.Position, qb.Position, qc.Position, qd.Position))
            {
                var plane = AxisCoordinate(axis, qa.Position);
                var planarKey = new PlanarQuadKey(
                    axis,
                    layer,
                    (long)Math.Round(plane * 10000.0),
                    tag,
                    positiveNormal);
                if (!planarQuads.TryGetValue(planarKey, out var quads))
                {
                    quads = workspace.RentPlanarGroup();
                    planarQuads[planarKey] = quads;
                }
                quads[(u, v)] = new PlanarQuad(qa.Position, qb.Position, qc.Position, qd.Position);
                return;
            }

            if (!positiveNormal) (qb, qd) = (qd, qb);
            AddTriangle(qa.Position, qb.Position, qc.Position, tag);
            AddTriangle(qa.Position, qc.Position, qd.Position, tag);
        }

        void EmitPlanarQuads()
        {
            foreach (var group in planarQuads)
            {
                var quads = group.Value;
                var remaining = workspace.RemainingQuads;
                remaining.Clear();
                remaining.UnionWith(quads.Keys);
                // The next rectangle always starts at the first remaining
                // row/column. Sort once: rescanning all surviving quads for
                // every rectangle made stepped/curved boundaries quadratic.
                var ordered = workspace.OrderedQuads;
                ordered.Clear();
                ordered.AddRange(quads.Keys);
                ordered.Sort(static (a, b) => a.V != b.V ? a.V.CompareTo(b.V) : a.U.CompareTo(b.U));
                foreach (var start in ordered)
                {
                    if (!remaining.Contains(start)) continue;
                    var width = 1;
                    while (remaining.Contains((start.U + width, start.V))) width++;
                    var height = 1;
                    while (true)
                    {
                        var nextV = start.V + height;
                        var completeRow = true;
                        for (var offset = 0; offset < width; offset++)
                            if (!remaining.Contains((start.U + offset, nextV)))
                            {
                                completeRow = false;
                                break;
                            }
                        if (!completeRow) break;
                        height++;
                    }

                    for (var dv = 0; dv < height; dv++)
                    for (var du = 0; du < width; du++)
                        remaining.Remove((start.U + du, start.V + dv));

                    var first = quads[start];
                    var alongU = quads[(start.U + width - 1, start.V)];
                    var opposite = quads[(start.U + width - 1, start.V + height - 1)];
                    var alongV = quads[(start.U, start.V + height - 1)];
                    var a = first.A;
                    var b = alongU.B;
                    var c = opposite.C;
                    var d = alongV.D;
                    if (!group.Key.PositiveNormal) (b, d) = (d, b);
                    AddTriangle(a, b, c, group.Key.Tag);
                    AddTriangle(a, c, d, group.Key.Tag);
                }
            }
        }

        int ResolveTargetTag(Vector3 center, int tag, ZDexelTarget? targetModel)
        {
            return targetModel?.IsInterior(center, TargetPitch * 0.75) == true ? -1 : tag;
        }

        void AddTriangle(Vector3 a, Vector3 b, Vector3 c, int tag)
        {
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() <= 1e-14f) return;
            normal = Vector3.Normalize(normal);
            if (!builders.TryGetValue(tag, out var builder))
            {
                builder = workspace.RentBuilder();
                builders[tag] = builder;
            }
            builder.AddTriangle(a, b, c, normal);
        }
    }

    private HashSet<SurfaceEdge> CollectSurfaceEdges(
        SurfaceChunkWorkspace workspace,
        int xStart,
        int xEnd,
        int yStart,
        int yEnd,
        int zStart,
        int zEnd)
    {
        var edges = workspace.Edges;

        for (var z = zStart; z < Math.Min(zEnd, _zCoordinates.Length); z++)
        for (var y = yStart; y < Math.Min(yEnd, _yCoordinates.Length); y++)
        foreach (var span in _xField.Ray(y, z).Spans)
        {
            AddX(span.Min, y, z);
            AddX(span.Max, y, z);
        }

        for (var z = zStart; z < Math.Min(zEnd, _zCoordinates.Length); z++)
        for (var x = xStart; x < Math.Min(xEnd, _xCoordinates.Length); x++)
        foreach (var span in _yField.Ray(x, z).Spans)
        {
            AddY(span.Min, x, z);
            AddY(span.Max, x, z);
        }

        for (var y = yStart; y < Math.Min(yEnd, _yCoordinates.Length); y++)
        for (var x = xStart; x < Math.Min(xEnd, _xCoordinates.Length); x++)
        foreach (var span in _zField.Ray(x, y).Spans)
        {
            AddZ(span.Min, x, y);
            AddZ(span.Max, x, y);
        }

        // Lattice occupancy is the union of all three dexel bundles. An edge
        // can therefore change because a transverse ray changes even when the
        // longitudinal bundle has no endpoint at that position. Compare only
        // neighbouring rays whose interval lists differ, then visit their
        // symmetric samples. This restores the exact dense-grid surface while
        // keeping uniform interior ray pairs O(1).
        for (var z = zStart; z < Math.Min(zEnd, _zCoordinates.Length); z++)
        for (var x = xStart; x < xEnd; x++)
        {
            DexelRay? a = ValidIndex(x - 1, _xCoordinates) ? _yField.Ray(x - 1, z) : null;
            DexelRay? b = ValidIndex(x, _xCoordinates) ? _yField.Ray(x, z) : null;
            if (EquivalentSpans(a, b)) continue;
            var differences = SampleMask(a, _yCoordinates, yStart, yEnd) ^
                SampleMask(b, _yCoordinates, yStart, yEnd);
            while (differences != 0)
            {
                var y = yStart + BitOperations.TrailingZeroCount(differences);
                differences &= differences - 1;
                TryX(x, y, z);
            }
        }

        for (var y = yStart; y < Math.Min(yEnd, _yCoordinates.Length); y++)
        for (var x = xStart; x < xEnd; x++)
        {
            DexelRay? a = ValidIndex(x - 1, _xCoordinates) ? _zField.Ray(x - 1, y) : null;
            DexelRay? b = ValidIndex(x, _xCoordinates) ? _zField.Ray(x, y) : null;
            if (EquivalentSpans(a, b)) continue;
            var differences = SampleMask(a, _zCoordinates, zStart, zEnd) ^
                SampleMask(b, _zCoordinates, zStart, zEnd);
            while (differences != 0)
            {
                var z = zStart + BitOperations.TrailingZeroCount(differences);
                differences &= differences - 1;
                TryX(x, y, z);
            }
        }

        for (var z = zStart; z < Math.Min(zEnd, _zCoordinates.Length); z++)
        for (var y = yStart; y < yEnd; y++)
        {
            DexelRay? a = ValidIndex(y - 1, _yCoordinates) ? _xField.Ray(y - 1, z) : null;
            DexelRay? b = ValidIndex(y, _yCoordinates) ? _xField.Ray(y, z) : null;
            if (EquivalentSpans(a, b)) continue;
            var differences = SampleMask(a, _xCoordinates, xStart, xEnd) ^
                SampleMask(b, _xCoordinates, xStart, xEnd);
            while (differences != 0)
            {
                var x = xStart + BitOperations.TrailingZeroCount(differences);
                differences &= differences - 1;
                TryY(y, x, z);
            }
        }

        for (var x = xStart; x < Math.Min(xEnd, _xCoordinates.Length); x++)
        for (var y = yStart; y < yEnd; y++)
        {
            DexelRay? a = ValidIndex(y - 1, _yCoordinates) ? _zField.Ray(x, y - 1) : null;
            DexelRay? b = ValidIndex(y, _yCoordinates) ? _zField.Ray(x, y) : null;
            if (EquivalentSpans(a, b)) continue;
            var differences = SampleMask(a, _zCoordinates, zStart, zEnd) ^
                SampleMask(b, _zCoordinates, zStart, zEnd);
            while (differences != 0)
            {
                var z = zStart + BitOperations.TrailingZeroCount(differences);
                differences &= differences - 1;
                TryY(y, x, z);
            }
        }

        for (var y = yStart; y < Math.Min(yEnd, _yCoordinates.Length); y++)
        for (var z = zStart; z < zEnd; z++)
        {
            DexelRay? a = ValidIndex(z - 1, _zCoordinates) ? _xField.Ray(y, z - 1) : null;
            DexelRay? b = ValidIndex(z, _zCoordinates) ? _xField.Ray(y, z) : null;
            if (EquivalentSpans(a, b)) continue;
            var differences = SampleMask(a, _xCoordinates, xStart, xEnd) ^
                SampleMask(b, _xCoordinates, xStart, xEnd);
            while (differences != 0)
            {
                var x = xStart + BitOperations.TrailingZeroCount(differences);
                differences &= differences - 1;
                TryZ(z, x, y);
            }
        }

        for (var x = xStart; x < Math.Min(xEnd, _xCoordinates.Length); x++)
        for (var z = zStart; z < zEnd; z++)
        {
            DexelRay? a = ValidIndex(z - 1, _zCoordinates) ? _yField.Ray(x, z - 1) : null;
            DexelRay? b = ValidIndex(z, _zCoordinates) ? _yField.Ray(x, z) : null;
            if (EquivalentSpans(a, b)) continue;
            var differences = SampleMask(a, _yCoordinates, yStart, yEnd) ^
                SampleMask(b, _yCoordinates, yStart, yEnd);
            while (differences != 0)
            {
                var y = yStart + BitOperations.TrailingZeroCount(differences);
                differences &= differences - 1;
                TryZ(z, x, y);
            }
        }

        return edges;

        void AddX(double boundary, int y, int z)
        {
            var first = SurfaceBoundaryIndex(_xCoordinates, boundary, xStart, xEnd);
            if (first < 0) return;
            TryX(first, y, z);
            TryX(first + 1, y, z);
        }

        void TryX(int x, int y, int z)
        {
            if (x < xStart || x >= xEnd || x < 0 || x > _xCoordinates.Length) return;
            var a = workspace.Lattice(x - 1, y, z);
            var b = workspace.Lattice(x, y, z);
            if (a.Occupied == b.Occupied) return;
            var crossing = EdgeVertex(a, b);
            edges.Add(new SurfaceEdge(SurfaceAxis.X, x, y, z, a.Occupied, crossing.Tag));
        }

        void AddY(double boundary, int x, int z)
        {
            var first = SurfaceBoundaryIndex(_yCoordinates, boundary, yStart, yEnd);
            if (first < 0) return;
            TryY(first, x, z);
            TryY(first + 1, x, z);
        }

        void TryY(int y, int x, int z)
        {
            if (y < yStart || y >= yEnd || y < 0 || y > _yCoordinates.Length) return;
            var a = workspace.Lattice(x, y - 1, z);
            var b = workspace.Lattice(x, y, z);
            if (a.Occupied == b.Occupied) return;
            var crossing = EdgeVertex(a, b);
            edges.Add(new SurfaceEdge(SurfaceAxis.Y, x, y, z, a.Occupied, crossing.Tag));
        }

        void AddZ(double boundary, int x, int y)
        {
            var first = SurfaceBoundaryIndex(_zCoordinates, boundary, zStart, zEnd);
            if (first < 0) return;
            TryZ(first, x, y);
            TryZ(first + 1, x, y);
        }

        void TryZ(int z, int x, int y)
        {
            if (z < zStart || z >= zEnd || z < 0 || z > _zCoordinates.Length) return;
            var a = workspace.Lattice(x, y, z - 1);
            var b = workspace.Lattice(x, y, z);
            if (a.Occupied == b.Occupied) return;
            var crossing = EdgeVertex(a, b);
            edges.Add(new SurfaceEdge(SurfaceAxis.Z, x, y, z, a.Occupied, crossing.Tag));
        }
    }

    // An endpoint can affect its lower-bound edge and the following edge.
    // Search only this tile plus that one-edge halo. Endpoints elsewhere on a
    // long stock ray cannot create a crossing in the current surface tile.
    private static int SurfaceBoundaryIndex(double[] coordinates, double boundary, int start, int end)
    {
        int low=Math.Max(0,start-1), high=Math.Min(coordinates.Length,end);
        if(low>0 && boundary<=coordinates[low-1])return -1;
        if(high<coordinates.Length && high>0 && boundary>coordinates[high-1])return -1;
        while(low<high)
        {
            int middle=low+(high-low)/2;
            if(coordinates[middle]<boundary)low=middle+1; else high=middle;
        }
        return low;
    }

    // A surface tile owns at most 32 samples along each ray. Compare their
    // exact occupancy as bits instead of scanning every sample on every pair
    // of neighbouring rays. End point epsilon is the same as DexelRay.Contains.
    private static uint SampleMask(DexelRay? ray, double[] coordinates, int start, int end)
    {
        if (!ray.HasValue) return 0;
        end = Math.Min(end, coordinates.Length);
        if (start >= end) return 0;
        uint mask = 0;
        foreach (var span in ray.Value.Spans)
        {
            const double epsilon = 1e-8;
            if (span.Max + epsilon < coordinates[start]) continue;
            if (span.Min - epsilon > coordinates[end - 1]) break;
            var first = span.Min - epsilon <= coordinates[start] ? start : LowerBound(coordinates, span.Min - epsilon);
            var last = span.Max + epsilon >= coordinates[end - 1] ? end : UpperBound(coordinates, span.Max + epsilon);
            if (first < last)
                mask |= (uint)(((1UL << (last - first)) - 1) << (first - start));
        }
        return mask;
    }

    private static bool EquivalentSpans(DexelRay? a, DexelRay? b)
    {
        if (!a.HasValue || !b.HasValue) return a.HasValue == b.HasValue;
        var left = a.Value;
        var right = b.Value;
        if (left.Spans.Count != right.Spans.Count) return false;
        for (var index = 0; index < left.Spans.Count; index++)
            if (Math.Abs(left.Spans[index].Min - right.Spans[index].Min) > 1e-9 ||
                Math.Abs(left.Spans[index].Max - right.Spans[index].Max) > 1e-9)
                return false;
        return true;
    }

    private void DiscoverSurfaceChunkKeys(HashSet<IpwChunkKey> destination)
    {
        for (var z = 0; z < _zCoordinates.Length; z++)
        for (var y = 0; y < _yCoordinates.Length; y++)
        foreach (var span in _xField.Ray(y, z).Spans)
        {
            AddX(span.Min, y, z);
            AddX(span.Max, y, z);
        }

        for (var z = 0; z < _zCoordinates.Length; z++)
        for (var x = 0; x < _xCoordinates.Length; x++)
        foreach (var span in _yField.Ray(x, z).Spans)
        {
            AddY(span.Min, x, z);
            AddY(span.Max, x, z);
        }

        for (var y = 0; y < _yCoordinates.Length; y++)
        for (var x = 0; x < _xCoordinates.Length; x++)
        foreach (var span in _zField.Ray(x, y).Spans)
        {
            AddZ(span.Min, x, y);
            AddZ(span.Max, x, y);
        }

        void AddX(double boundary, int y, int z)
        {
            var first = LowerBound(_xCoordinates, boundary);
            Add(first, y, z);
            Add(first + 1, y, z);
        }

        void AddY(double boundary, int x, int z)
        {
            var first = LowerBound(_yCoordinates, boundary);
            Add(x, first, z);
            Add(x, first + 1, z);
        }

        void AddZ(double boundary, int x, int y)
        {
            var first = LowerBound(_zCoordinates, boundary);
            Add(x, y, first);
            Add(x, y, first + 1);
        }

        void Add(int x, int y, int z)
        {
            if (x < 0 || x > _xCoordinates.Length ||
                y < 0 || y > _yCoordinates.Length ||
                z < 0 || z > _zCoordinates.Length)
                return;
            destination.Add(new IpwChunkKey(
                x / SurfaceChunkCells,
                y / SurfaceChunkCells,
                z / SurfaceChunkCells));
        }
    }

    private void TrackSurfaceChunk(IpwSurfaceChunk chunk)
    {
        if (chunk.Meshes.Count == 0) _knownSurfaceChunks.Remove(chunk.Key);
        else _knownSurfaceChunks.Add(chunk.Key);
    }

    private bool IsAxisPlanar(
        SurfaceAxis axis,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d)
    {
        var coordinateA = AxisCoordinate(axis, a);
        var coordinateB = AxisCoordinate(axis, b);
        var coordinateC = AxisCoordinate(axis, c);
        var coordinateD = AxisCoordinate(axis, d);
        var minimum = Math.Min(Math.Min(coordinateA, coordinateB), Math.Min(coordinateC, coordinateD));
        var maximum = Math.Max(Math.Max(coordinateA, coordinateB), Math.Max(coordinateC, coordinateD));
        return maximum - minimum <= Math.Max(1e-6, TargetPitch * 1e-5);
    }

    private static float AxisCoordinate(SurfaceAxis axis, Vector3 point) => axis switch
    {
        SurfaceAxis.X => point.X,
        SurfaceAxis.Y => point.Y,
        _ => point.Z
    };

    private static Vector3 AxisNormal(SurfaceAxis axis) => axis switch
    {
        SurfaceAxis.X => Vector3.UnitX,
        SurfaceAxis.Y => Vector3.UnitY,
        _ => Vector3.UnitZ
    };

    private SurfaceVertex? BuildCellVertex(SurfaceChunkWorkspace workspace, int xCell, int yCell, int zCell)
    {
        Span<LatticeVertex> vertices = stackalloc LatticeVertex[8];
        CellVertices(workspace, xCell, yCell, zCell, vertices);
        var occupiedCount = 0;
        for (var index = 0; index < vertices.Length; index++)
            if (vertices[index].Occupied)
                occupiedCount++;
        if (occupiedCount is 0 or 8) return null;
        Span<SurfaceVertex> crossings = stackalloc SurfaceVertex[12];
        var crossingCount = 0;
        foreach (var edge in CellEdges)
            if (vertices[edge.A].Occupied != vertices[edge.B].Occupied)
                crossings[crossingCount++] = EdgeVertex(vertices[edge.A], vertices[edge.B]);
        if (crossingCount == 0) return null;

        var position = Vector3.Zero;
        for (var index = 0; index < crossingCount; index++)
            position += crossings[index].Position;
        position /= crossingCount;
        return new SurfaceVertex(position, ResolveSurfaceTag(crossings[..crossingCount]));
    }

    private static void CellVertices(SurfaceChunkWorkspace workspace, int xCell, int yCell, int zCell, Span<LatticeVertex> vertices)
    {
        var x = xCell - 1;
        var y = yCell - 1;
        var z = zCell - 1;
        vertices[0] = workspace.Lattice(x,     y,     z);
        vertices[1] = workspace.Lattice(x + 1, y,     z);
        vertices[2] = workspace.Lattice(x + 1, y + 1, z);
        vertices[3] = workspace.Lattice(x,     y + 1, z);
        vertices[4] = workspace.Lattice(x,     y,     z + 1);
        vertices[5] = workspace.Lattice(x + 1, y,     z + 1);
        vertices[6] = workspace.Lattice(x + 1, y + 1, z + 1);
        vertices[7] = workspace.Lattice(x,     y + 1, z + 1);
    }

    private LatticeVertex Lattice(int xIndex, int yIndex, int zIndex)
    {
        var point = new Vector3(
            (float)LatticeCoordinate(_xCoordinates, xIndex),
            (float)LatticeCoordinate(_yCoordinates, yIndex),
            (float)LatticeCoordinate(_zCoordinates, zIndex));
        var validX = ValidIndex(xIndex, _xCoordinates);
        var validY = ValidIndex(yIndex, _yCoordinates);
        var validZ = ValidIndex(zIndex, _zCoordinates);
        // A render sample is accepted when at least two independent dexel
        // bundles agree that material exists.  Using a raw union (one of three)
        // turns the discretisation residue of a single bundle into the long
        // needle/stripe artefacts visible on machined faces.  Majority fusion
        // removes those direction-dependent outliers while keeping the three
        // interval fields themselves—and therefore stock accuracy—unchanged.
        var occupancyVotes = 0;
        if (validY && validZ && _xField.Ray(yIndex, zIndex).Contains(point.X)) occupancyVotes++;
        if (validX && validZ && _yField.Ray(xIndex, zIndex).Contains(point.Y)) occupancyVotes++;
        if (validX && validY && _zField.Ray(xIndex, yIndex).Contains(point.Z)) occupancyVotes++;
        var occupied = validX && validY && validZ && occupancyVotes >= 2;
        return new LatticeVertex(xIndex, yIndex, zIndex, point, occupied);
    }

    private SurfaceVertex EdgeVertex(LatticeVertex a, LatticeVertex b)
    {
        if (a.XIndex != b.XIndex && ValidIndex(a.YIndex, _yCoordinates) && ValidIndex(a.ZIndex, _zCoordinates))
        {
            var minimum = Math.Min(a.Position.X, b.Position.X);
            var maximum = Math.Max(a.Position.X, b.Position.X);
            var minimumOccupied = a.Position.X <= b.Position.X ? a.Occupied : b.Occupied;
            _xField.Ray(a.YIndex, a.ZIndex).TryBoundary(minimum, maximum, minimumOccupied, out var value, out var tag);
            return new SurfaceVertex(new Vector3((float)value, a.Position.Y, a.Position.Z), tag);
        }
        if (a.YIndex != b.YIndex && ValidIndex(a.XIndex, _xCoordinates) && ValidIndex(a.ZIndex, _zCoordinates))
        {
            var minimum = Math.Min(a.Position.Y, b.Position.Y);
            var maximum = Math.Max(a.Position.Y, b.Position.Y);
            var minimumOccupied = a.Position.Y <= b.Position.Y ? a.Occupied : b.Occupied;
            _yField.Ray(a.XIndex, a.ZIndex).TryBoundary(minimum, maximum, minimumOccupied, out var value, out var tag);
            return new SurfaceVertex(new Vector3(a.Position.X, (float)value, a.Position.Z), tag);
        }
        if (a.ZIndex != b.ZIndex && ValidIndex(a.XIndex, _xCoordinates) && ValidIndex(a.YIndex, _yCoordinates))
        {
            var minimum = Math.Min(a.Position.Z, b.Position.Z);
            var maximum = Math.Max(a.Position.Z, b.Position.Z);
            var minimumOccupied = a.Position.Z <= b.Position.Z ? a.Occupied : b.Occupied;
            _zField.Ray(a.XIndex, a.YIndex).TryBoundary(minimum, maximum, minimumOccupied, out var value, out var tag);
            return new SurfaceVertex(new Vector3(a.Position.X, a.Position.Y, (float)value), tag);
        }
        return new SurfaceVertex((a.Position + b.Position) * 0.5f, 0);
    }

    private static int ResolveSurfaceTag(
        SurfaceVertex a,
        SurfaceVertex b,
        SurfaceVertex c,
        SurfaceVertex d)
    {
        Span<SurfaceVertex> vertices = stackalloc SurfaceVertex[4] { a, b, c, d };
        return ResolveSurfaceTag(vertices);
    }

    private static int ResolveSurfaceTag(ReadOnlySpan<SurfaceVertex> vertices)
    {
        var bestTag = 0;
        var bestCount = 0;
        for (var index = 0; index < vertices.Length; index++)
        {
            var tag = vertices[index].Tag;
            if (tag <= 0) continue;
            var count = 0;
            for (var candidate = 0; candidate < vertices.Length; candidate++)
                if (vertices[candidate].Tag == tag)
                    count++;
            if (count > bestCount || (count == bestCount && tag > bestTag))
            {
                bestCount = count;
                bestTag = tag;
            }
        }
        return bestTag;
    }

    private static int NearestIndex(IReadOnlyList<double> coordinates, double value)
    {
        var upper = LowerBound(coordinates, value);
        if (upper <= 0) return 0;
        if (upper >= coordinates.Count) return coordinates.Count - 1;
        return Math.Abs(coordinates[upper] - value) < Math.Abs(value - coordinates[upper - 1]) ? upper : upper - 1;
    }

    private static double LatticeCoordinate(IReadOnlyList<double> coordinates, int index)
    {
        if (index < 0) return coordinates[0] - (coordinates[1] - coordinates[0]);
        if (index >= coordinates.Count) return coordinates[^1] + (coordinates[^1] - coordinates[^2]);
        return coordinates[index];
    }

    private static bool ValidIndex(int index, IReadOnlyList<double> coordinates) =>
        index >= 0 && index < coordinates.Count;

    private static int DirtyCellMinimum(IReadOnlyList<double> coordinates, double value) =>
        Math.Clamp(LowerBound(coordinates, value) - 1, 0, coordinates.Count);

    private static int DirtyCellMaximum(IReadOnlyList<double> coordinates, double value) =>
        Math.Clamp(UpperBound(coordinates, value) + 1, 0, coordinates.Count);

    private static int ChunkCount(int cells) => (cells + SurfaceChunkCells - 1) / SurfaceChunkCells;

    private readonly record struct LatticeVertex(
        int XIndex,
        int YIndex,
        int ZIndex,
        Vector3 Position,
        bool Occupied);

    private readonly record struct SurfaceVertex(Vector3 Position, int Tag);

    /// <summary>
    /// Scratch space for one committed chunk and its seam halo. Occupancy is
    /// evaluated once per lattice point, not once per adjoining edge/cell.
    /// Nothing survives a build except reusable storage: reset/cut/tag changes
    /// always read the current authoritative fields. Each parallel build owns
    /// a separate workspace and the retained pool has a hardware-neutral cap.
    /// </summary>
    private sealed class SurfaceChunkWorkspace : IDisposable
    {
        private const int Side = SurfaceChunkCells + 2;
        private const int Capacity = Side * Side * Side;
        private static readonly ConcurrentBag<SurfaceChunkWorkspace> Pool = new();
        private static int _pooledCount;
        private readonly byte[] _flags = new byte[Capacity];
        private readonly SurfaceVertex[] _vertices = new SurfaceVertex[Capacity];
        public List<IpwChunkKey> RequiredCells { get; } = new();
        public HashSet<SurfaceEdge> Edges { get; } = new();
        public Dictionary<int, SurfaceMeshBuilder> Builders { get; } = new();
        public Dictionary<PlanarQuadKey, Dictionary<(int U, int V), PlanarQuad>> PlanarGroups { get; } = new();
        public HashSet<(int U, int V)> RemainingQuads { get; } = new();
        public List<(int U, int V)> OrderedQuads { get; } = new();
        private readonly Stack<Dictionary<(int U, int V), PlanarQuad>> _planarPool = new();
        private readonly Stack<SurfaceMeshBuilder> _builderPool = new();
        private TripleDexelStock _stock = null!;
        private int _xOrigin, _yOrigin, _zOrigin;

        public static SurfaceChunkWorkspace Rent(TripleDexelStock stock, int x, int y, int z)
        {
            if (Pool.TryTake(out var workspace)) Interlocked.Decrement(ref _pooledCount);
            else workspace = new SurfaceChunkWorkspace();
            workspace._stock = stock;
            workspace._xOrigin = x - 1;
            workspace._yOrigin = y - 1;
            workspace._zOrigin = z - 1;
            return workspace;
        }

        private int Index(int x, int y, int z) =>
            ((z - _zOrigin) * Side + (y - _yOrigin)) * Side + (x - _xOrigin);

        public LatticeVertex Lattice(int x, int y, int z)
        {
            var index = Index(x, y, z);
            var flags = _flags[index];
            if ((flags & 1) == 0)
            {
                var value = _stock.Lattice(x, y, z);
                _flags[index] = (byte)(flags | 1 | (value.Occupied ? 2 : 0));
                return value;
            }
            return new LatticeVertex(x, y, z, new Vector3(
                (float)LatticeCoordinate(_stock._xCoordinates, x),
                (float)LatticeCoordinate(_stock._yCoordinates, y),
                (float)LatticeCoordinate(_stock._zCoordinates, z)), (flags & 2) != 0);
        }

        public void RequireCell(int x, int y, int z)
        {
            var index = Index(x, y, z);
            if ((_flags[index] & 4) != 0) return;
            _flags[index] |= 4;
            RequiredCells.Add(new IpwChunkKey(x, y, z));
        }

        public void SetCellVertex(int x, int y, int z, SurfaceVertex vertex)
        {
            var index = Index(x, y, z);
            _vertices[index] = vertex;
            _flags[index] |= 8;
        }

        public SurfaceVertex? CellVertex(int x, int y, int z)
        {
            var index = Index(x, y, z);
            return (_flags[index] & 8) != 0 ? _vertices[index] : null;
        }

        public Dictionary<(int U, int V), PlanarQuad> RentPlanarGroup() =>
            _planarPool.TryPop(out var group) ? group : new();

        public SurfaceMeshBuilder RentBuilder() =>
            _builderPool.TryPop(out var builder) ? builder : new();

        public void Dispose()
        {
            _stock = null!;
            RequiredCells.Clear();
            Edges.Clear();
            RemainingQuads.Clear();
            OrderedQuads.Clear();
            foreach (var group in PlanarGroups.Values)
            {
                group.Clear();
                if (_planarPool.Count < 128) _planarPool.Push(group);
            }
            PlanarGroups.Clear();
            foreach (var builder in Builders.Values)
            {
                builder.Clear();
                if (_builderPool.Count < 16) _builderPool.Push(builder);
            }
            Builders.Clear();
            Array.Clear(_flags);
            if (Interlocked.Increment(ref _pooledCount) <= SurfaceMeshingParallelOptions.MaxDegreeOfParallelism + 1)
                Pool.Add(this);
            else
                Interlocked.Decrement(ref _pooledCount);
        }
    }

    private enum SurfaceAxis { X, Y, Z }

    private readonly record struct SurfaceEdge(
        SurfaceAxis Axis,
        int X,
        int Y,
        int Z,
        bool LowerOccupied,
        int Tag);

    private readonly record struct PlanarQuadKey(
        SurfaceAxis Axis,
        int Layer,
        long Plane,
        int Tag,
        bool PositiveNormal);

    private readonly record struct PlanarQuad(Vector3 A, Vector3 B, Vector3 C, Vector3 D);

    private sealed class SurfaceMeshBuilder
    {
        private readonly List<float> _positions = new();
        private readonly List<float> _normals = new();
        private readonly List<int> _indices = new();
        private Vector3 _minimum = new(float.PositiveInfinity);
        private Vector3 _maximum = new(float.NegativeInfinity);
        public int TriangleCount => _indices.Count / 3;

        public void Clear()
        {
            _positions.Clear();
            _normals.Clear();
            _indices.Clear();
            _minimum = new(float.PositiveInfinity);
            _maximum = new(float.NegativeInfinity);
        }

        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            Add(a, normal); Add(b, normal); Add(c, normal);
        }

        public TriangleMeshData Build() => new()
        {
            Positions = _positions.ToArray(),
            Normals = _normals.ToArray(),
            Indices = _indices.ToArray(),
            Bounds = new Bounds3(_minimum, _maximum)
        };

        private void Add(Vector3 point, Vector3 normal)
        {
            _positions.Add(point.X); _positions.Add(point.Y); _positions.Add(point.Z);
            _normals.Add(normal.X); _normals.Add(normal.Y); _normals.Add(normal.Z);
            _indices.Add(_indices.Count);
            _minimum = Vector3.Min(_minimum, point);
            _maximum = Vector3.Max(_maximum, point);
        }
    }
}
