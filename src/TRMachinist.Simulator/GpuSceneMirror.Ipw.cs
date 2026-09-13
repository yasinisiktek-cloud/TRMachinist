using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.Wpf.SharpDX;
using TRMachinist.Core;
using DxMeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using Color = System.Windows.Media.Color;

namespace TRMachinist.Simulator;

internal sealed partial class GpuSceneMirror
{
    // Rendering groups do not change stock cells, vertices, normals or tags.
    // Keep one CPU geometry per group; regional entries store ranges into it,
    // rather than retaining a second complete copy for resume after pause.
    private readonly record struct IpwMeshKey(IpwChunkKey Chunk, int Tag);
    private readonly record struct IpwBatchKey(int X, int Y, int Z, int Tag);
    private sealed class IpwEntry
    {
        public int VertexStart, VertexCount, IndexStart, IndexCount;
        public TriangleMeshData? Pending;
    }
    private sealed class IpwBatch
    {
        public readonly HashSet<IpwMeshKey> Members = new();
        public DxMeshGeometry3D Geometry = new() { IsDynamic = true };
        public DxMeshGeometry3D? PreparedGeometry;
        public MeshGeometryModel3D? Model;
        public Color Color;
    }
    private readonly Dictionary<IpwMeshKey, IpwEntry> _ipwEntries = new();
    private readonly Dictionary<IpwChunkKey, HashSet<int>> _chunkTags = new();
    private readonly Dictionary<IpwBatchKey, IpwBatch> _ipwBatches = new();
    private readonly HashSet<IpwBatchKey> _dirtyIpwBatches = new();
    private bool _deferIpwPublication;
    private bool _stageIpwPublication;
    private readonly HashSet<IpwBatchKey> _preparedIpwBatches = new();
    public int IpwMeshCount => _ipwVisible ? _ipwEntries.Count : 0;
    public int IpwDrawModelCount => _ipwVisible ? _ipwBatches.Count : 0;
    public int PendingIpwVertexCount => _dirtyIpwBatches.Sum(key =>
        _ipwBatches.TryGetValue(key, out var batch)
            ? batch.Members.Sum(member => _ipwEntries[member].Pending?.Positions.Length / 3
                ?? _ipwEntries[member].VertexCount) : 0);

    private static IpwBatchKey BatchKey(IpwMeshKey key) => new(
        (int)Math.Floor(key.Chunk.X / 4.0), (int)Math.Floor(key.Chunk.Y / 4.0),
        (int)Math.Floor(key.Chunk.Z / 4.0), key.Tag);

    public void BeginIpwPublication(bool stage = false)
    {
        _deferIpwPublication = true;
        _stageIpwPublication = stage;
    }

    public void BeginIpwReplacement()
    {
        // Reset/rewind starts a complete new generation. Retire every old
        // region at commit, including deletions still queued in the old worker.
        // Keep the previous rendered buffers visible throughout preparation.
        _ipwEntries.Clear(); _chunkTags.Clear(); _dirtyIpwBatches.Clear();
        _preparedIpwBatches.Clear();
        foreach (var (key, batch) in _ipwBatches)
        {
            batch.Members.Clear(); batch.PreparedGeometry = null;
            _preparedIpwBatches.Add(key);
        }
        _stageIpwPublication = true;
    }
    public void EndIpwPublication()
    {
        _deferIpwPublication = false;
        FlushIpwBatches();
        if (!_stageIpwPublication) CommitIpwPublication();
    }

    public void PublishIpwChunks(IReadOnlyList<MachineSceneController.PreparedIpwVisualChunk> chunks,
        Matrix3D worldTransform, double opacity)
    {
        if (_disposed || chunks.Count == 0) return;
        _ipwOpacity = Math.Clamp(opacity, 0.10, 1.0);
        SetIpwTransform(worldTransform);
        foreach (var prepared in chunks)
        {
            var chunk = prepared.Surface;
            var tags = chunk.Meshes.Where(m => m.Mesh.Indices.Length > 0).Select(m => m.Tag).ToHashSet();
            if (_chunkTags.TryGetValue(chunk.Key, out var previous))
                foreach (var tag in previous.Except(tags))
                {
                    var key = new IpwMeshKey(chunk.Key, tag);
                    _ipwEntries.Remove(key);
                    var batchKey = BatchKey(key);
                    if (_ipwBatches.TryGetValue(batchKey, out var batch)) batch.Members.Remove(key);
                    _dirtyIpwBatches.Add(batchKey);
                }
            foreach (var tagged in chunk.Meshes)
            {
                if (tagged.Mesh.Indices.Length == 0) continue;
                var key = new IpwMeshKey(chunk.Key, tagged.Tag);
                if (!_ipwEntries.TryGetValue(key, out var entry)) _ipwEntries[key] = entry = new();
                entry.Pending = tagged.Mesh;
                var batchKey = BatchKey(key);
                if (!_ipwBatches.TryGetValue(batchKey, out var batch)) _ipwBatches[batchKey] = batch = new();
                batch.Members.Add(key);
                _dirtyIpwBatches.Add(batchKey);
            }
            if (tags.Count > 0) _chunkTags[chunk.Key] = tags;
            else _chunkTags.Remove(chunk.Key);
        }
        if (!_deferIpwPublication)
        {
            FlushIpwBatches();
            if (!_stageIpwPublication) CommitIpwPublication();
        }
    }

    private void FlushIpwBatches()
    {
        if (_disposed || _dirtyIpwBatches.Count == 0) return;
        foreach (var key in _dirtyIpwBatches)
        {
            if (!_ipwBatches.TryGetValue(key, out var batch)) continue;
            if (batch.Members.Count == 0)
            {
                _preparedIpwBatches.Add(key);
                continue;
            }
            var entries = batch.Members.Select(k => _ipwEntries[k]).ToArray();
            var vertexCount = entries.Sum(e => e.Pending?.Positions.Length / 3 ?? e.VertexCount);
            var indexCount = entries.Sum(e => e.Pending?.Indices.Length ?? e.IndexCount);
            var positions = new Vector3Collection(vertexCount);
            var normals = new Vector3Collection(vertexCount);
            var indices = new IntCollection(indexCount);
            var old = batch.PreparedGeometry ?? batch.Geometry;
            foreach (var entry in entries)
            {
                var start = positions.Count;
                var indexStart = indices.Count;
                if (entry.Pending is { } mesh)
                {
                    for (var i = 0; i < mesh.Positions.Length; i += 3)
                    {
                        positions.Add(new(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]));
                        var normal = mesh.Normals.Length > i + 2
                            ? new System.Numerics.Vector3(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2])
                            : System.Numerics.Vector3.UnitZ;
                        normals.Add(normal.LengthSquared() > 1e-12f
                            ? System.Numerics.Vector3.Normalize(normal) : System.Numerics.Vector3.UnitZ);
                    }
                    foreach (var index in mesh.Indices) indices.Add(start + index);
                    entry.Pending = null;
                }
                else
                {
                    for (var i = 0; i < entry.VertexCount; i++)
                    {
                        positions.Add(old.Positions![entry.VertexStart + i]);
                        normals.Add(old.Normals![entry.VertexStart + i]);
                    }
                    for (var i = 0; i < entry.IndexCount; i++)
                        indices.Add(start + old.Indices![entry.IndexStart + i] - entry.VertexStart);
                }
                entry.VertexStart = start; entry.VertexCount = positions.Count - start;
                entry.IndexStart = indexStart; entry.IndexCount = indices.Count - indexStart;
            }
            batch.PreparedGeometry = new DxMeshGeometry3D
                { Positions = positions, Normals = normals, Indices = indices };
            _preparedIpwBatches.Add(key);
        }
        _dirtyIpwBatches.Clear();
    }

    public void CommitIpwPublication()
    {
        // All changed regions become visible in one render frame. While they
        // are being assembled the previous complete stock stays on screen.
        foreach (var key in _preparedIpwBatches)
        {
            if (!_ipwBatches.TryGetValue(key, out var batch)) continue;
            if (batch.Members.Count == 0)
            {
                if (batch.Model is not null) { _ipwRoot.Children.Remove(batch.Model); batch.Model.Dispose(); }
                _ipwBatches.Remove(key);
                continue;
            }
            if (batch.PreparedGeometry is not { } prepared) continue;
            var old = batch.Geometry;
            old.Positions = prepared.Positions; old.Normals = prepared.Normals; old.Indices = prepared.Indices;
            old.UpdateVertices(); old.UpdateTriangles(); old.UpdateBounds();
            batch.PreparedGeometry = null;
            var color = Alpha4SurfaceColor(key.Tag, _ipwOpacity);
            if (batch.Model is null)
            {
                batch.Model = CreateIpwModel(old, color);
                batch.Color = color;
                _ipwRoot.Children.Add(batch.Model);
            }
            else if (batch.Color != color)
            {
                batch.Color = color;
                batch.Model.Material = CreateMaterial(color, glossy: true);
                batch.Model.IsTransparent = color.A < 255;
            }
            batch.Model.Visibility = _ipwVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        _preparedIpwBatches.Clear();
        _stageIpwPublication = false;
        _viewport.InvalidateRender();
    }

    public void CompactIpw() { FlushIpwBatches(); CommitIpwPublication(); }
    private void DisposeIpwBatches()
    {
        foreach (var batch in _ipwBatches.Values)
            if (batch.Model is not null) { _ipwRoot.Children.Remove(batch.Model); batch.Model.Dispose(); }
        _ipwBatches.Clear(); _dirtyIpwBatches.Clear(); _deferIpwPublication = false;
        _preparedIpwBatches.Clear(); _stageIpwPublication = false;
    }
    public void ClearIpw()
    {
        DisposeIpwBatches(); _ipwEntries.Clear(); _chunkTags.Clear();
        _viewport.InvalidateSceneGraph(); _viewport.InvalidateRender();
    }
    public void SetIpwVisible(bool visible)
    {
        _ipwVisible = visible;
        foreach (var batch in _ipwBatches.Values)
            if (batch.Model is not null) batch.Model.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _viewport.InvalidateRender();
    }
}
