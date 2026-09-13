using System.Numerics;
using System.Windows;
using Color = System.Windows.Media.Color;
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using TRMachinist.Core;

namespace TRMachinist.Simulator;

internal sealed partial class GpuSceneMirror
{
    private const int PathPageSize = 4096;
    private readonly List<PathGroup> _pathGroups = new();
    private bool? _lastPathProgressive;
    private sealed record PathPage(int Start, int Count, LineGeometryModel3D Model, LineGeometry3D Geometry)
    {
        public int PublishedCount { get; set; } = -1;
    }
    private sealed record PathGroup(GCodePathTiming[] Timings, Vector3[] Starts, List<PathPage> Pages,
        LineGeometryModel3D Head, LineGeometry3D HeadGeometry);

    private void CreateOperationPath(GCodeOperationPathPreview data)
    {
        DisposeChildren(_operationPathRoot);
        _pathGroups.Clear();
        _lastPathProgressive = null;
        Add(data.CuttingIndices, data.CuttingTimings, Color.FromRgb(30, 100, 230), 1.9);
        Add(data.LinkingIndices, data.LinkingTimings, Color.FromRgb(177, 99, 22), 1.05);
        UpdateOperationPathProgress(false, 0, 0, Vector3.Zero);
        SetOperationPathVisible(_operationPathVisible);

        void Add(int[] indices, GCodePathTiming[] timings, Color color, double thickness)
        {
            if (indices.Length == 0) return;
            var count = indices.Length / 2;
            var starts = new Vector3[count];
            var pages = new List<PathPage>();
            for (var start = 0; start < count; start += PathPageSize)
            {
                var size = Math.Min(PathPageSize, count - start);
                var positions = new Vector3Collection(size * 2);
                for (var segment = start; segment < start + size; segment++)
                {
                    starts[segment] = Point(indices[segment * 2]);
                    positions.Add(starts[segment]);
                    positions.Add(Point(indices[segment * 2 + 1]));
                }
                var geometry = new LineGeometry3D { Positions = positions, Indices = new IntCollection(), IsDynamic = true,
                    PreDefinedVertexCount = size * 2, PreDefinedIndexCount = size * 2 };
                var model = MakeModel(geometry, color, thickness);
                _operationPathRoot.Children.Add(model);
                pages.Add(new(start, size, model, geometry));
            }
            var headGeometry = new LineGeometry3D { Positions = new Vector3Collection { Vector3.Zero, Vector3.Zero },
                Indices = new IntCollection { 0, 1 }, IsDynamic = true, PreDefinedVertexCount = 2, PreDefinedIndexCount = 2 };
            var head = MakeModel(headGeometry, color, thickness);
            _operationPathRoot.Children.Add(head);
            _pathGroups.Add(new(timings, starts, pages, head, headGeometry));
        }
        Vector3 Point(int index) => new(data.Positions[index * 3], data.Positions[index * 3 + 1], data.Positions[index * 3 + 2]);
    }

    // Completed pages never upload again during forward playback. Only the
    // current page's bounded index prefix and its two-vertex head can change.
    public void UpdateOperationPathProgress(bool progressive, int blockIndex, double progress, Vector3 currentTip)
    {
        if (!progressive && _lastPathProgressive == false) return;
        _lastPathProgressive = progressive;
        foreach (var group in _pathGroups)
        {
            var prefix = progressive ? GCodePathPrefix.At(group.Timings, blockIndex, progress)
                : new GCodePathPrefix(group.Starts.Length, 0);
            foreach (var page in group.Pages)
            {
                var count = Math.Clamp(prefix.CompletedSegments - page.Start, 0, page.Count);
                page.Model.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (page.PublishedCount == count) continue;
                page.Geometry.Indices = new IntCollection(Enumerable.Range(0, count * 2));
                page.Geometry.UpdateTriangles();
                page.PublishedCount = count;
            }
            group.Head.Visibility = prefix.PartialProgress > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (prefix.PartialProgress > 0)
            {
                group.HeadGeometry.Positions = new Vector3Collection { group.Starts[prefix.CompletedSegments], currentTip };
                group.HeadGeometry.UpdateVertices();
                group.HeadGeometry.UpdateBounds();
            }
        }
        _viewport.InvalidateRender();
    }

    private static LineGeometryModel3D MakeModel(LineGeometry3D geometry, Color color, double thickness) => new()
    {
        Geometry = geometry, Color = color, Thickness = thickness, Smoothness = 1.0,
        DepthBias = -64, SlopeScaledDepthBias = -2, IsHitTestVisible = false
    };

    private static void DisposeChildren(GroupModel3D group)
    {
        var children = group.Children.ToArray();
        group.Children.Clear();
        foreach (var child in children) child.Dispose();
    }
}


