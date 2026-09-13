using System.Collections.Concurrent;
using System.Diagnostics;
using TRMachinist.Core;

namespace TRMachinist.Simulator;

/// <summary>
/// Runs exact 0.15 mm stock subtraction and regional surface reconstruction
/// away from the dispatcher. Playback publishes only a desired NC cursor; the
/// UI receives raw changed-region buffers for DirectX upload.
/// </summary>
internal sealed class Alpha4IpwBackgroundPipeline : IAsyncDisposable
{
    public sealed record Snapshot(
        SimulationCursor Requested,
        SimulationCursor Processed,
        int PendingDisplayCuts,
        int PreparedVisualChunks,
        int DirtySurfaceChunks,
        int ReportRevision,
        int VisualGeneration,
        MachineSceneController.Alpha4IpwTestReport? LatestReport,
        double LastKernelMilliseconds,
        double TotalKernelMilliseconds,
        double LastSurfaceMilliseconds,
        bool IsKernelCaughtUp,
        bool IsSurfacePrepared,
        bool IsCaughtUp,
        string? Error);

    private sealed record PreparedVisual(
        long Sequence,
        MachineSceneController.PreparedIpwVisualChunk Chunk);

    private const int VisualPreparationBatchSize = 12;
    private const int MaximumKernelBlockBatch = 24;

    private readonly MachineSceneController _scene;
    private readonly GCodeProgram _program;
    private readonly object _gate = new();
    private readonly object _visualBuildGate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _visualSignal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<IpwChunkKey, PreparedVisual>
        _preparedVisuals = new();
    private readonly Task _worker;
    private readonly Task _visualWorker;

    private SimulationCursor _requested;
    private SimulationCursor _processed;
    private bool _resetRequested;
    private bool _resetInProgress;
    private bool _kernelInProgress;
    private bool _visualBatchInFlight;
    private long _kernelSequence;
    private int _reportRevision;
    private MachineSceneController.Alpha4IpwTestReport? _latestReport;
    private double _lastKernelMilliseconds;
    private double _totalKernelMilliseconds;
    private double _lastSurfaceMilliseconds;
    private int _dirtySurfaceChunks;
    private long _preparedVisualSequence;
    private bool _visualSourceDrained = true;
    private bool _visualPreparationPaused;
    private int _visualGeneration;
    private string? _error;
    private bool _disposed;

    public Alpha4IpwBackgroundPipeline(MachineSceneController scene, GCodeProgram program)
    {
        _scene = scene;
        _program = program;
        _scene.ConfigureAlpha4NcProgram(program);
        _requested = StartCursor();
        _processed = StartCursor();
        _worker = Task.Factory.StartNew(
            WorkerAsync,
            _cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        _visualWorker = Task.Factory.StartNew(
            VisualWorkerAsync,
            _cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        PulseVisual();
    }

    public void Request(int blockIndex, double progress)
    {
        if (_program.Blocks.Count == 0) return;
        // A completed block (N, 100%) and the next block at its beginning
        // (N+1, 0%) are the same stock cursor.  The worker stores the latter,
        // while SimulationPlayer deliberately displays the former for one
        // frame.  Comparing their raw forms made every completed block look
        // like a rewind and reset the exact IPW kernel back to blank.
        var cursor = new SimulationCursor(blockIndex, progress).Normalize(_program.Blocks.Count);
        lock (_gate)
        {
            _processed = _processed.Normalize(_program.Blocks.Count);
            // Lowering a not-yet-processed future request needs no reset; the
            // worker can simply stop at the new target. Reset only when the
            // requested stock state is genuinely behind committed material.
            if (SimulationCursor.Compare(cursor, _processed) < 0)
                _resetRequested = true;
            _requested = cursor;
        }
        Pulse();
    }

    public void RequestReset()
    {
        lock (_gate)
        {
            _requested = StartCursor();
            _resetRequested = true;
        }
        Pulse();
    }

    public Snapshot GetSnapshot()
    {
        lock (_gate)
        {
            var kernelCaughtUp = !_resetRequested && !_resetInProgress && !_kernelInProgress &&
                SimulationCursor.Compare(_processed, _requested) >= 0;
            var pendingDisplayCuts = _scene.Alpha4PendingDisplayCutCount;
            return new Snapshot(
                _requested,
                _processed,
                pendingDisplayCuts,
                _preparedVisuals.Count,
                _dirtySurfaceChunks,
                _reportRevision,
                _visualGeneration,
                _latestReport,
                _lastKernelMilliseconds,
                _totalKernelMilliseconds,
                _lastSurfaceMilliseconds,
                kernelCaughtUp,
                kernelCaughtUp && !_visualBatchInFlight && pendingDisplayCuts == 0 && _visualSourceDrained,
                kernelCaughtUp &&
                !_visualBatchInFlight &&
                pendingDisplayCuts == 0 &&
                _preparedVisuals.IsEmpty &&
                _visualSourceDrained,
                _error);
        }
    }

    public void SetVisualPreparationPaused(bool paused)
    {
        var changed = false;
        lock (_gate)
        {
            if (_visualPreparationPaused != paused)
            {
                _visualPreparationPaused = paused;
                changed = true;
            }
        }
        if (changed && !paused) PulseVisual();
    }

    public IReadOnlyList<MachineSceneController.PreparedIpwVisualChunk> TakePreparedVisuals(int maximumCount)
    {
        if (maximumCount <= 0 || _preparedVisuals.IsEmpty)
            return Array.Empty<MachineSceneController.PreparedIpwVisualChunk>();

        var result = new List<MachineSceneController.PreparedIpwVisualChunk>(maximumCount);
        var available = _preparedVisuals.ToArray();
        // Drain a complete spatial GPU group at a time. Random regional order
        // repeatedly copied/uploaded the same large group for each tiny edit.
        var first = available.MinBy(pair => pair.Value.Sequence);
        var group = Group(first.Key);
        var ordered = available.Where(pair => Group(pair.Key) == group)
            .OrderBy(pair => pair.Key.Z).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X).ToArray();
        var requestedCount = Math.Min(maximumCount, ordered.Length);
        IEnumerable<KeyValuePair<IpwChunkKey, PreparedVisual>> selected;
        if (requestedCount >= ordered.Length)
        {
            selected = ordered;
        }
        else
        {
            // Mirror the mesher's fair queue: keep most bandwidth around the
            // current cutter but guarantee that its completed trail cannot be
            // starved by a continuous stream of newer chunks.
            var newestCount = Math.Max(1, (requestedCount * 2 + 2) / 3);
            var oldestCount = requestedCount - newestCount;
            selected = ordered.Take(newestCount)
                .Concat(ordered.Skip(ordered.Length - oldestCount).Reverse());
        }

        foreach (var entry in selected)
        {
            if (_preparedVisuals.TryRemove(entry.Key, out var prepared))
                result.Add(prepared.Chunk);
        }
        return result;

        static (int X, int Y, int Z) Group(IpwChunkKey key) => (key.X / 4, key.Y / 4, key.Z / 4);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        Pulse();
        PulseVisual();
        try
        {
            await Task.WhenAll(_worker, _visualWorker).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        _signal.Dispose();
        _visualSignal.Dispose();
        _cancellation.Dispose();
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                while (!_cancellation.IsCancellationRequested)
                {
                    SimulationCursor target;
                    bool reset;
                    lock (_gate)
                    {
                        target = _requested;
                        reset = _resetRequested;
                        _resetRequested = false;
                        if (reset)
                        {
                            _resetInProgress = true;
                            _visualSourceDrained = false;
                            _kernelSequence++;
                            _visualGeneration++;
                            _preparedVisuals.Clear();
                        }
                    }

                    if (reset)
                    {
                        var resetWatch = Stopwatch.StartNew();
                        MachineSceneController.Alpha4IpwTestReport report;
                        // Finish/discard an old regional build before resetting
                        // stock. It must never consume new-generation dirtiness.
                        lock (_visualBuildGate) report = _scene.ResetAlpha4IpwKernel();
                        resetWatch.Stop();
                        lock (_gate)
                        {
                            _processed = StartCursor();
                            _resetInProgress = false;
                            _latestReport = report;
                            _lastKernelMilliseconds = resetWatch.Elapsed.TotalMilliseconds;
                            _totalKernelMilliseconds += resetWatch.Elapsed.TotalMilliseconds;
                            _visualSourceDrained = false;
                            _dirtySurfaceChunks = _scene.Alpha4PendingVisualChunkCount;
                            _reportRevision++;
                            target = _requested;
                        }
                        PulseVisual();
                    }

                    var didKernelWork = AdvanceOneStep(target);
                    if (didKernelWork)
                        PulseVisual();

                    // Recheck the latest requested cursor after every bounded
                    // unit. This coalesces dozens of 60 Hz UI requests into one
                    // forward stock stream without ever dropping a cut segment.
                    lock (_gate) target = _requested;
                    if (didKernelWork || !IsProcessedAtLeast(target))
                        continue;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
                _error = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private async Task VisualWorkerAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await _visualSignal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                while (!_cancellation.IsCancellationRequested)
                {
                    lock (_gate)
                        if (_visualPreparationPaused)
                            break;
                    var preparedCount = PrepareVisualBatch();
                    var pendingCuts = _scene.Alpha4PendingDisplayCutCount;
                    var dirtyChunks = _scene.Alpha4PendingVisualChunkCount;
                    if (preparedCount == 0 &&
                        pendingCuts == 0 && dirtyChunks == 0)
                        break;

                    // Let the exact 0.15 mm cutter worker and DirectX upload
                    // path run between bounded regional surface batches.
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
                _error = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private bool AdvanceOneStep(SimulationCursor target)
    {
        SimulationCursor from;
        lock (_gate)
        {
            _processed = _processed.Normalize(_program.Blocks.Count);
            from = _processed;
        }
        if (SimulationCursor.Compare(from, target) >= 0 || from.BlockIndex >= _program.Blocks.Count)
            return false;
        var toProgress = from.BlockIndex < target.BlockIndex ? 1.0 : target.Progress;
        if (toProgress <= from.Progress + 1e-9) return false;

        lock (_gate)
        {
            _kernelInProgress = true;
            _kernelSequence++;
            _visualSourceDrained = false;
        }
        try
        {

            var batchCount = CountBatchableFullBlocks(from, target);
            if (batchCount >= 2)
            {
                var batchWatch = Stopwatch.StartNew();
                var batch = _program.Blocks.Skip(from.BlockIndex).Take(batchCount).ToArray();
                var batchReport = _scene.ApplyAlpha4NcBlockBatch(batch);
                batchWatch.Stop();
                var batchDirtySurfaceChunks = _scene.Alpha4PendingVisualChunkCount;
                lock (_gate)
                {
                    _processed = new SimulationCursor(from.BlockIndex + batchCount, 0)
                        .Normalize(_program.Blocks.Count);
                    _latestReport = batchReport;
                    _lastKernelMilliseconds = batchWatch.Elapsed.TotalMilliseconds;
                    _totalKernelMilliseconds += batchWatch.Elapsed.TotalMilliseconds;
                    // Only the visual worker can confirm a fully enqueued surface.
                    _visualSourceDrained = false;
                    _dirtySurfaceChunks = batchDirtySurfaceChunks;
                    _reportRevision++;
                }
                return true;
            }

            var watch = Stopwatch.StartNew();
            var report = _scene.ApplyAlpha4NcBlockProgress(
                _program.Blocks[from.BlockIndex],
                from.Progress,
                toProgress,
                refreshVisual: false);
            watch.Stop();
            var dirtySurfaceChunks = _scene.Alpha4PendingVisualChunkCount;

            lock (_gate)
            {
                _processed = new SimulationCursor(from.BlockIndex, toProgress).Normalize(_program.Blocks.Count);
                _latestReport = report;
                _lastKernelMilliseconds = watch.Elapsed.TotalMilliseconds;
                _totalKernelMilliseconds += watch.Elapsed.TotalMilliseconds;
                _visualSourceDrained = false;
                _dirtySurfaceChunks = dirtySurfaceChunks;
                _reportRevision++;
            }
            return true;
        }
        finally
        {
            lock (_gate) _kernelInProgress = false;
        }
    }

    private int CountBatchableFullBlocks(SimulationCursor from, SimulationCursor target)
    {
        if (from.Progress > 1e-9 || from.BlockIndex >= target.BlockIndex)
            return 0;
        var available = Math.Min(
            MaximumKernelBlockBatch,
            Math.Min(target.BlockIndex - from.BlockIndex, _program.Blocks.Count - from.BlockIndex));
        if (available < 2) return 0;

        var tool = _program.Blocks[from.BlockIndex].Tool;
        var count = 0;
        for (; count < available; count++)
        {
            var block = _program.Blocks[from.BlockIndex + count];
            if (!MachineSceneController.CanBatchAlpha4NcBlock(block, tool)) break;
        }
        return count;
    }

    private int PrepareVisualBatch()
    {
        lock (_visualBuildGate)
        {
            int generation;
            long sequence;
            lock (_gate)
            {
                if (_resetInProgress) return 0;
                generation = _visualGeneration;
                sequence = _kernelSequence;
                _visualBatchInFlight = true;
            }
            try
            {
                var watch = Stopwatch.StartNew();
                var prepared = _scene.PrepareAlpha4IpwVisualChunks(VisualPreparationBatchSize);
                watch.Stop();
                var dirtySurfaceChunks = _scene.Alpha4PendingVisualChunkCount;
                var pendingDisplayCuts = _scene.Alpha4PendingDisplayCutCount;
                lock (_gate)
                {
                    // Generation check and enqueue are one transaction with reset's
                    // clear. A completed old worker must never reinsert stale regions.
                    if (generation != _visualGeneration) return 0;
                    foreach (var chunk in prepared)
                        _preparedVisuals[chunk.Key] = new PreparedVisual(++_preparedVisualSequence, chunk);
                    if (prepared.Count > 0) _lastSurfaceMilliseconds = watch.Elapsed.TotalMilliseconds;
                    _visualSourceDrained = !_kernelInProgress && sequence == _kernelSequence &&
                        pendingDisplayCuts == 0 && dirtySurfaceChunks == 0;
                    _dirtySurfaceChunks = dirtySurfaceChunks;
                }
                return prepared.Count;
            }
            finally
            {
                lock (_gate) _visualBatchInFlight = false;
            }
        }
    }

    private bool IsProcessedAtLeast(SimulationCursor target)
    {
        lock (_gate)
            return SimulationCursor.Compare(
                _processed.Normalize(_program.Blocks.Count),
                target.Normalize(_program.Blocks.Count)) >= 0;
    }

    private SimulationCursor StartCursor() => new(0, 0);

    private void Pulse()
    {
        if (_disposed && _cancellation.IsCancellationRequested) return;
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void PulseVisual()
    {
        if (_disposed && _cancellation.IsCancellationRequested) return;
        try
        {
            if (_visualSignal.CurrentCount == 0) _visualSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
