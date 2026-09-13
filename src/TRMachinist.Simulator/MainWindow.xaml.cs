using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using TRMachinist.Core;
using DxOrthographicCamera = HelixToolkit.Wpf.SharpDX.OrthographicCamera;

namespace TRMachinist.Simulator;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record CollisionLogEntry(
        string EventKey,
        string Type,
        string SourceLine,
        string First,
        string Second,
        string Clearance);

    private const int WmMouseWheel = 0x020A;
    private const int MaximumPreparedVisualChunksPerFrame = 96;
    private static readonly TimeSpan PreparedVisualPublishBudget = TimeSpan.FromMilliseconds(6);
    private static readonly TimeSpan ViewportInteractionGrace = TimeSpan.FromMilliseconds(120);
    private readonly SimulationPlayer _player = new();
    private readonly AdaptivePlaybackGovernor _playbackGovernor = new();
    private readonly DispatcherTimer _timer;
    private MachineSceneController? _scene;
    private MachinePackage? _machine;
    private JobPackage? _job;
    private GCodeProgram? _program;
    private DateTime _lastTick;
    private string? _activeTool;
    private string? _ncPath;
    private bool _syncingSelection;
    private bool _refreshingVisibility;
    private bool _nxPanActive;
    private bool _nxPanAwaitingFullRelease;
    private bool _nxMiddleHeld;
    private bool _nxRightHeld;
    private Point _nxPanLast;
    private Alpha4IpwBackgroundPipeline? _ipwPipeline;
    private readonly GpuSceneMirror _gpuScene;
    private int _lastIpwReportRevision = -1;
    private DateTime _lastIpwReportDisplay;
    private bool _ipwPipelineErrorShown;
    private bool _restoringStockOpacity;
    private double _acceptedStockOpacityPercent = 100;
    private DateTime _lastViewportInteractionUtc = DateTime.MinValue;
    private bool _interactiveRenderMode;
    private DateTime _lastPlaybackUiRefreshUtc = DateTime.MinValue;
    private GCodeSimulationContext? _simulationContext;
    private bool _ipwVisible = true;
    private bool _toolPathVisible;
    private IReadOnlyList<GCodeOperationRange> _toolPathOperations = Array.Empty<GCodeOperationRange>();
    private int _toolPathBuildRevision;
    private bool _syncingOperationSelection;
    private CancellationTokenSource? _toolPathSelectionDebounce;
    // The NC player may calculate its next pose ahead of the scene, but that
    // pose is not published until the exact stock kernel and every dirty GPU
    // surface chunk have reached the same cursor.  The visible cutter can
    // therefore never outrun the swept stock surface.
    private bool _ipwPresentationPending;
    private double _lastIpwPublishMilliseconds;
    private readonly ObservableCollection<CollisionLogEntry> _collisionHistory = new();
    private readonly HashSet<string> _activeCollisionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collisionHistoryEventKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCollisionUiRefreshUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        CollisionHistoryGrid.ItemsSource = _collisionHistory;
        GpuViewport.EffectsManager = new DefaultEffectsManager();
        GpuViewport.Camera = new DxOrthographicCamera
        {
            Position = new Point3D(850, -1100, 760),
            LookDirection = new Vector3D(-850, 1100, -560),
            UpDirection = new Vector3D(0, 0, 1),
            Width = 2200
        };
        ConfigureNxMouseNavigation();
        // Viewport3DX owns a native DirectX input surface. On this workstation
        // WM_MOUSEWHEEL is consumed before WPF can raise PreviewMouseWheel,
        // which made every routed-event-only fix a no-op. Intercept the native
        // message on the UI thread, as NX does for its graphics window.
        ComponentDispatcher.ThreadPreprocessMessage += ThreadPreprocessMessage;
        _scene = new MachineSceneController(Viewport);
        _gpuScene = new GpuSceneMirror(Viewport, GpuViewport, GpuSceneRoot);
        _scene.Alpha4VisualChunksPublished += PublishGpuIpwChunks;
        _scene.Alpha4VisualCleared += _gpuScene.ClearIpw;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        RefreshWorkspacePresentation();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _toolPathBuildRevision++;
        ComponentDispatcher.ThreadPreprocessMessage -= ThreadPreprocessMessage;
        _toolPathSelectionDebounce?.Cancel();
        _toolPathSelectionDebounce?.Dispose();
        await StopIpwPipelineAsync();
        _scene!.Alpha4VisualChunksPublished -= PublishGpuIpwChunks;
        _scene.Alpha4VisualCleared -= _gpuScene.ClearIpw;
        _gpuScene.Dispose();
        GpuViewport.EffectsManager?.Dispose();
    }

    private void ConfigureNxMouseNavigation()
    {
        // Test9 renders through Viewport3DX. The old NX bindings were still
        // attached to the collapsed WPF viewport, leaving the visible scene
        // with Helix's default mouse map. Install the NX map on the renderer
        // that actually receives input:
        //   MMB drag       = orbit
        //   MMB + RMB drag = pan
        //   wheel forward  = zoom out
        //   wheel backward = zoom in
        GpuViewport.CameraMode = HelixToolkit.SharpDX.CameraMode.Inspect;
        GpuViewport.CameraRotationMode = HelixToolkit.SharpDX.CameraRotationMode.Trackball;
        GpuViewport.IsInertiaEnabled = false;
        GpuViewport.InfiniteSpin = false;
        GpuViewport.SpinReleaseTime = 0;
        GpuViewport.ModelUpDirection = new Vector3D(0, 0, 1);
        GpuViewport.RotateAroundMouseDownPoint = false;
        GpuViewport.FixedRotationPointEnabled = false;
        GpuViewport.IsRotationEnabled = true;
        GpuViewport.IsPanEnabled = true;
        // Do not delegate the wheel direction to Helix. Its WPF and native
        // swap-chain routes use different signs on this workstation, which is
        // why changing ZoomSensitivity did not change the user's result.
        // The routed wheel handler below changes orthographic Width directly:
        // positive delta (wheel forward) = wider/farther, negative = nearer.
        GpuViewport.IsZoomEnabled = false;
        GpuViewport.ZoomAroundMouseDownPoint = false;

        ICommand[] replacedCommands =
        [
            ViewportCommands.Rotate,
            ViewportCommands.Pan,
            ViewportCommands.Zoom,
            ViewportCommands.ZoomRectangle,
            ViewportCommands.ChangeFieldOfView
        ];
        for (var index = GpuViewport.InputBindings.Count - 1; index >= 0; index--)
        {
            if (GpuViewport.InputBindings[index] is InputBinding binding &&
                binding.Command is not null && replacedCommands.Contains(binding.Command))
                GpuViewport.InputBindings.RemoveAt(index);
        }
        GpuViewport.InputBindings.Add(
            new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.MiddleClick)));

        GpuViewport.PreviewMouseDown += Viewport_PreviewMouseDown;
        GpuViewport.PreviewMouseMove += Viewport_PreviewMouseMove;
        // The native swap-chain child raises the bubbling MouseUp event, not
        // PreviewMouseUp.  Listening here prevents a released middle button
        // from remaining latched in the orbit handler.
        GpuViewport.MouseUp += Viewport_MouseUp;

        var fit = new MenuItem { Header = "Görünüme sığdır" };
        fit.Click += (_, _) => _gpuScene?.ZoomExtents();
        var hideCabin = new MenuItem { Header = "Dış kabini gizle" };
        hideCabin.Click += HideCabin_Click;
        var showAll = new MenuItem { Header = "Tüm komponentleri göster" };
        showAll.Click += ShowAll_Click;
        Viewport.ContextMenu = new ContextMenu();
        Viewport.ContextMenu.Items.Add(fit);
        Viewport.ContextMenu.Items.Add(hideCabin);
        Viewport.ContextMenu.Items.Add(showAll);
        GpuViewport.ContextMenu = Viewport.ContextMenu;
    }

    private void ThreadPreprocessMessage(ref MSG message, ref bool handled)
    {
        if (handled || message.message != WmMouseWheel || !IsActive) return;

        var packedPoint = message.lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packedPoint & 0xffff)),
            unchecked((short)((packedPoint >> 16) & 0xffff)));
        var viewportPoint = GpuViewport.PointFromScreen(screenPoint);
        if (viewportPoint.X < 0 || viewportPoint.Y < 0 ||
            viewportPoint.X > GpuViewport.ActualWidth ||
            viewportPoint.Y > GpuViewport.ActualHeight) return;

        var delta = unchecked((short)((message.wParam.ToInt64() >> 16) & 0xffff));
        if (delta == 0) return;
        MarkViewportInteraction();
        ApplyMouseWheelZoom(delta);
        handled = true;
    }

    private void ApplyMouseWheelZoom(int delta)
    {
        if (GpuViewport.Camera is not DxOrthographicCamera camera) return;

        GpuViewport.StopSpin();
        var detents = delta / 120.0;
        var scale = Math.Pow(1.18, detents);
        camera.Width = Math.Clamp(camera.Width * scale, 0.5, 100_000.0);
        GpuViewport.InvalidateRender();
    }

    private void PublishGpuIpwChunks(
        IReadOnlyList<MachineSceneController.PreparedIpwVisualChunk> chunks)
    {
        if (_scene is null) return;
        _gpuScene.PublishIpwChunks(chunks, _scene.Alpha4IpwWorldTransform, _scene.StockOpacity);
    }

    private void Viewport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) _nxMiddleHeld = true;
        if (e.ChangedButton == MouseButton.Right) _nxRightHeld = true;
        if (_nxMiddleHeld || _nxRightHeld) MarkViewportInteraction();

        if (_nxMiddleHeld && _nxRightHeld)
        {
            GpuViewport.StopSpin();
            _nxPanActive = true;
            _nxPanAwaitingFullRelease = false;
            GpuViewport.IsRotationEnabled = false;
            _nxPanLast = e.GetPosition(GpuViewport);
            e.Handled = true;
        }
    }

    private void Viewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            MarkViewportInteraction();
        if (!_nxPanActive)
        {
            if (_nxPanAwaitingFullRelease &&
                e.MiddleButton == MouseButtonState.Released &&
                e.RightButton == MouseButtonState.Released)
                FinishNxPanGesture();
            return;
        }
        if (e.MiddleButton != MouseButtonState.Pressed || e.RightButton != MouseButtonState.Pressed)
        {
            _nxPanActive = false;
            _nxPanAwaitingFullRelease = true;
            if (e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
                FinishNxPanGesture();
            e.Handled = true;
            return;
        }

        var current = e.GetPosition(GpuViewport);
        PanCamera(current - _nxPanLast);
        _nxPanLast = current;
        e.Handled = true;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) _nxMiddleHeld = false;
        else if (e.ChangedButton == MouseButton.Right) _nxRightHeld = false;
        else return;

        MarkViewportInteraction();
        _gpuScene.UpdateCameraDepthRange();

        if (!_nxPanActive && !_nxPanAwaitingFullRelease) return;
        _nxPanActive = false;
        _nxPanAwaitingFullRelease = true;
        if (!_nxMiddleHeld && !_nxRightHeld)
            FinishNxPanGesture();

        // Let Helix observe the middle-button release so its own orbit handler
        // cannot retain a stale pressed state. Right release is ours alone.
        e.Handled = e.ChangedButton == MouseButton.Right;
    }

    private void MarkViewportInteraction() => _lastViewportInteractionUtc = DateTime.UtcNow;

    private bool IsViewportInteractionActive(DateTime now) =>
        _nxPanActive || _nxMiddleHeld || _nxRightHeld ||
        now - _lastViewportInteractionUtc < ViewportInteractionGrace;

    private void FinishNxPanGesture()
    {
        _nxPanActive = false;
        _nxPanAwaitingFullRelease = false;
        _nxMiddleHeld = false;
        _nxRightHeld = false;
        GpuViewport.StopSpin();
        GpuViewport.IsRotationEnabled = true;
        if (Mouse.Captured == GpuViewport) Mouse.Capture(null);
    }

    private void PanCamera(System.Windows.Vector screenDelta)
    {
        if (GpuViewport.Camera is not DxOrthographicCamera camera || screenDelta.LengthSquared < 0.01) return;
        var look = camera.LookDirection;
        var up = camera.UpDirection;
        if (look.LengthSquared < 1e-12 || up.LengthSquared < 1e-12) return;
        look.Normalize();
        up.Normalize();
        var right = Vector3D.CrossProduct(look, up);
        if (right.LengthSquared < 1e-12) return;
        right.Normalize();
        up = Vector3D.CrossProduct(right, look);
        up.Normalize();

        // Orthographic panning has no picked depth.  Its scale comes only from
        // the visible width, so an arbitrary point in empty space can never
        // become the movement reference.  The fixed interaction pivot remains
        // the live table center installed by UpdateNxRotationPivot().
        var worldPerPixel = Math.Max(camera.Width, 0.001) / Math.Max(GpuViewport.ActualWidth, 1.0);
        var translation = ((-right * screenDelta.X) + (up * screenDelta.Y)) * worldPerPixel;
        camera.Position += translation;
    }

    private void UpdateNxRotationPivot()
    {
        var tableCenter = _scene?.CurrentTableCenter;
        if (tableCenter is null)
        {
            GpuViewport.FixedRotationPointEnabled = false;
            return;
        }
        GpuViewport.FixedRotationPoint = tableCenter.Value;
        GpuViewport.FixedRotationPointEnabled = true;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var args = ResolveStartupPaths();
        try
        {
            foreach (var path in args.Where(IsMachinePackage)) await LoadMachineAsync(path);
            foreach (var path in args.Where(IsJobPackage)) await LoadJobAsync(path);
            foreach (var path in args.Where(path => !IsMachinePackage(path) && !IsJobPackage(path))) await LoadNcAsync(path);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void OpenMachine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "TRMachinist makine paketini aç",
            Filter = "TRMachinist makine paketi (*.trmac)|*.trmac|Tüm dosyalar (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await LoadMachineAsync(dialog.FileName); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OpenJob_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "TRMachinist hızlı NX sim paketini aç",
            Filter = "TRMachinist hızlı NX sim paketi (*.trjob)|*.trjob|Eski ShopDoc paketi (*.shopdocv)|*.shopdocv|Tüm dosyalar (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await LoadJobAsync(dialog.FileName); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OpenNc_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Post edilmiş NC programını aç",
            Filter = "NC / G-code (*.nc;*.mpf;*.spf;*.h;*.tap;*.txt)|*.nc;*.mpf;*.spf;*.h;*.tap;*.txt|Tüm dosyalar (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { await LoadNcAsync(dialog.FileName); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task LoadMachineAsync(string path)
    {
        _player.Pause();
        ResetPathAndDiagnostics();
        await StopIpwPipelineAsync();
        SetBusy(true, ".trmac doğrulanıyor…");
        try
        {
            var machine = await Task.Run(() => MachinePackageReader.Load(path));
            if (!machine.HasRuntimeMeshes)
            {
                throw new InvalidDataException(
                    "Bu .trmac paketi yalnızca STEP içeriyor. Gerçek zamanlı görüntüleme için binary STL runtime mesh gerekir. " +
                    "U630 paketini TRMAC Extractor 0.4.5 veya üstüyle yeniden çıkarın.");
            }

            StatusText.Text = "Makine geometrisi kuruluyor…";
            _scene!.LoadMachine(machine, message => StatusText.Text = message);
            _gpuScene.ClearIpw();
            _gpuScene.ClearOperationPath();
            _gpuScene.SynchronizeStaticScene(forceRebuild: true);
            _gpuScene.ZoomExtents();
            UpdateNxRotationPivot();
            MachineOpacitySlider.Value = 100;
            _machine = machine;
            _job = null;
            _program = null;
            _simulationContext = null;
            _toolPathVisible = false;
            _toolPathOperations = Array.Empty<GCodeOperationRange>();
            _toolPathBuildRevision++;
            CancelToolPathSelectionDebounce();
            ToolPathButton.IsEnabled = false;
            ToolPathButton.Content = "Operasyonlar";
            OperationGrid.ItemsSource = null;
            ToolGrid.ItemsSource = null;
            OperationSummaryText.Text = "Önce NC programını açın.";
            OperationPathStatusText.Text = "Takım yolu kapalı.";
            ExecutionStatusText.Text = "Makine yüklendi; NX işi ve NC programı bekleniyor.";
            ResetCollisionState(clearHistory: true, "Makine hazır; iş ve NC programı bekleniyor.");
            IpwVisibilityButton.IsEnabled = false;
            IpwVisibilityButton.Content = "Stoku gizle";
            _player.Pause();
            MachineFileText.Text = $"{machine.MachineName}\n{Path.GetFileName(path)}\n{machine.ControllerFamily}";
            JobFileText.Text = "Henüz açılmadı";
            NcFileText.Text = "Henüz açılmadı";
            GCodeGrid.ItemsSource = null;
            TableFrameText.Text = FrameText(_scene.LastTableFrame ?? FindTableFrame(machine));
            MountFrameText.Text = "machineMountCsys bekleniyor";
            PlacementErrorText.Text = "—";
            CsysOffsetText.Text = "X 0 · Y 0 · Z 0 mm";
            RefreshVisibilityPanel();
            UpdateRuntime();
            Alpha4IpwInfoText.Text = "Hazır — doğru NX .trjob paketini açın.";
            StatusText.Text = $"Makine hazır: {machine.MachineName}. {_gpuScene.DiagnosticSummary()}. Şimdi hızlı NX .trjob paketini açın.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadJobAsync(string path)
    {
        _player.Pause();
        ResetPathAndDiagnostics();
        if (_machine is null) throw new InvalidOperationException("Önce U630 .trmac makine paketini açın.");
        _player.Pause();
        SetBusy(true, "Eski stok işçileri güvenle durduruluyor…");
        await Dispatcher.Yield(DispatcherPriority.Background);
        await StopIpwPipelineAsync();
        try
        {
            BusyText.Text = "NX .trjob sim paketi doğrulanıyor…";
            var job = await Task.Run(() => JobPackageReader.Load(path));
            if (!job.HasExplicitMachineMount)
                throw new InvalidDataException("NX sim paketinde project.machineMountCsys yok. NX aktarımında tabla merkezine oturtulacak CSYS'yi seçip paketi yeniden alın.");
            BusyText.Text = "Parça, blank ve fikstür arka planda hazırlanıyor…";
            StatusText.Text = "Büyük NX iş modelleri arka planda hazırlanıyor; pencere kullanılabilir kalacak…";
            var prepared = await Task.Run(() => _scene!.PrepareJob(job));
            BusyText.Text = "Hazırlanan NX işi ekrana bağlanıyor…";
            _scene!.LoadJob(prepared, message => StatusText.Text = message);
            _job = job;
            _program = null;
            _simulationContext = null;
            _player.Pause();
            _gpuScene.ClearOperationPath();
            _toolPathVisible = false;
            _toolPathOperations = Array.Empty<GCodeOperationRange>();
            _toolPathBuildRevision++;
            CancelToolPathSelectionDebounce();
            ToolPathButton.IsEnabled = false;
            ToolPathButton.Content = "Operasyonlar";
            OperationGrid.ItemsSource = null;
            ToolGrid.ItemsSource = _scene.EffectiveTools;
            OperationSummaryText.Text = "NC programı açıldığında operasyon sınırları eşleştirilecek.";
            OperationPathStatusText.Text = "Takım yolu kapalı.";
            ExecutionStatusText.Text = $"NX işi yüklendi: {job.Operations.Count} operasyon, {job.Tools.Count} takım. NC programı bekleniyor.";
            ResetCollisionState(clearHistory: true, "Makine ve iş hazır; NC programı bekleniyor.");
            IpwVisibilityButton.IsEnabled = false;
            IpwVisibilityButton.Content = "Stoku gizle";
            GCodeGrid.ItemsSource = null;
            NcFileText.Text = "Henüz açılmadı";
            _gpuScene.SynchronizeStaticScene(forceRebuild: true);
            _gpuScene.ZoomExtents();
            UpdateNxRotationPivot();
            var roles = string.Join(" + ", job.Models.Select(x => RoleLabel(x.Role)).Distinct(StringComparer.OrdinalIgnoreCase));
            JobFileText.Text = $"{job.PartNumber} — {job.PartName}\n{Path.GetFileName(path)}\n{job.Models.Count} mesh: {roles}";
            MountFrameText.Text = FrameText(job.MachineMount);
            TableFrameText.Text = FrameText(_scene.LastTableFrame!);
            var error = PlacementError(job);
            PlacementErrorText.Text = $"{error:0.000000} mm";
            PlacementErrorText.Foreground = error <= 0.001 ? (Brush)FindResource("ForegroundPrimary") : Brushes.OrangeRed;
            UpdateCsysOffsetText();
            RefreshVisibilityPanel();
            UpdateRuntime();
            UpdateStockOpacityAvailability();
            Alpha4IpwInfoText.Text = "Hazır — IPW başlat ile blank sınırlarından IPW kurun.";
            // Alpha 3 Test-18: the CAM MCS drives the whole visual motion path.
            // A captured NX CSE G54 that disagrees with it is a real setup
            // conflict in the NX machine setup and is reported, never silently
            // compensated with a world-space offset.
            var workOffsetDelta = job.HasExplicitControllerWorkFrame
                ? Vector3.Distance(job.Mcs.Origin, job.ControllerWorkFrame.Origin)
                : 0f;
            var workOffsetNote = workOffsetDelta > 0.001f
                ? $" UYARI: NX CSE kontrolör çerçevesi ile CAM MCS arasında {workOffsetDelta:0.###} mm iş ofseti farkı var."
                + " Görsel çözüm CAM MCS'ye göre yapılır; farkı NX makine kurulumundaki parça bağlama/sıfır ofsetinden düzeltin."
                : string.Empty;
            var seatNote = ReportMountFrameConsistency(job);
            StatusText.Text = $"NX işi yüklendi: {roles}. Seçilen CSYS doğrudan PART_MOUNT_JCT tabla merkezine bağlandı. Bindirme hatası: {error:0.000000} mm." + workOffsetNote + seatNote;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadNcAsync(string path)
    {
        _player.Pause();
        ResetPathAndDiagnostics();
        if (_machine is null) throw new InvalidOperationException("NC programından önce .trmac makine paketini açın.");
        await StopIpwPipelineAsync();
        SetBusy(true, "NC programı ayrıştırılıyor…");
        try
        {
            if (_scene is not null)
            {
                _scene.EndAlpha4Test2();
                _gpuScene.ClearIpw();
                _gpuScene.SynchronizeStaticScene(forceRebuild: true);
            }
            _ipwVisible = true;
            IpwVisibilityButton.IsEnabled = false;
            IpwVisibilityButton.Content = "Stoku gizle";
            _gpuScene.ClearOperationPath();
            _toolPathVisible = false;
            _toolPathOperations = Array.Empty<GCodeOperationRange>();
            _toolPathBuildRevision++;
            CancelToolPathSelectionDebounce();
            ToolPathButton.Content = "Operasyonlar";
            OperationGrid.ItemsSource = null;
            OperationPathStatusText.Text = "Takım yolu kapalı.";
            var simulationContext = BuildGCodeSimulationContext();
            _simulationContext = simulationContext;
            var program = await Task.Run(() => GCodeParser.Load(path, _machine, simulationContext));
            _program = program;
            _player.Load(program);
            _player.SpeedFactor = SpeedSlider.Value;
            ResetPlaybackGovernor();
            GCodeGrid.ItemsSource = program.SourceLines;
            RefreshNcDiagnostics();
            TimelineSlider.Minimum = 0;
            TimelineSlider.Maximum = Math.Max(0, program.Blocks.Count - 1);
            TimelineSlider.Value = 0;
            _ncPath = path;
            NcFileText.Text = Path.GetFileName(path);
            ProgramSummaryText.Text = $"{program.SourceLines.Count} kaynak satır · {program.Blocks.Count} yürütülen blok · {program.MotionCount} hareket · {program.WarningCount} uyarı · {program.ErrorCount} hata";
            _toolPathOperations = GCodeOperationCatalog.Build(program, _job?.Operations);
            OperationGrid.ItemsSource = _toolPathOperations;
            OperationGrid.SelectedIndex = _toolPathOperations.Count > 0
                ? GCodeOperationCatalog.FindOperationIndex(_toolPathOperations, 0)
                : -1;
            ToolPathButton.IsEnabled = _toolPathOperations.Count > 0;
            OperationSummaryText.Text = _toolPathOperations.Count > 0
                ? $"{_toolPathOperations.Count} operasyon bulundu. Tek seçim veya Ctrl/Shift ile çoklu seçim yapabilirsiniz."
                : "NC içinde operasyon sınırı veya hareketli takım bölümü bulunamadı.";
            ExecutionStatusText.Text = $"NC hazır: {program.SourceLines.Count} kaynak satırı, {program.Blocks.Count} blok, {_toolPathOperations.Count} operasyon, {program.WarningCount} uyarı, {program.ErrorCount} hata.";
            ResetCollisionState(clearHistory: true, "Kontrol hazır; oynatırken makine pozu izlenecek.");
            ApplyPlayerState(forceUiRefresh: true);
            UpdateStockOpacityAvailability();
            ToolPathVisibleCheckBox.IsEnabled = ProgressiveToolPathCheckBox.IsEnabled = _toolPathOperations.Count > 0;
            SetPathChecked(true);
            await ShowSelectedOperationPathAsync();
            StatusText.Text = program.ErrorCount == 0
                ? "NC programı hazır. IPW kapalıdır; normal OYNAT yalnız makine simülasyonunu çalıştırır. Stok kesimi için NC IPW BAŞLAT'a basın."
                : $"NC ayrıştırıldı; {program.ErrorCount} hata bulundu. Durum sekmesinde açıklamaları kontrol edin.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _inspectNcDiagnostic = false;
        if (_machine is null || _job is null || _program is null)
        {
            MessageBox.Show(this, "Simülasyon için üç veri de gerekli: .trmac makine + hızlı NX .trjob sim paketi + post edilmiş NC.",
                "Eksik veri", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_player.IsPlaying)
        {
            _player.Pause();
            if (_scene?.IsAlpha4Test2Active == true)
            {
                SyncAlpha4IpwToPlayer();
                StatusText.Text = "NC duraklatıldı; 0,15 mm IPW çekirdeği son konuma arka planda tamamlanıyor.";
            }
        }
        else
        {
            if (_player.BlockIndex >= _program.Blocks.Count) _player.Reset();
            _lastTick = DateTime.UtcNow;
            ResetPlaybackGovernor();
            _player.Play();
        }
        UpdatePlayButton();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _player.Reset();
        ResetCollisionState(clearHistory: true, "Simülasyon başa alındı; çarpışma kaydı temizlendi.");
        ResetPlaybackGovernor();
        ApplyPlayerState(forceUiRefresh: true);
        UpdatePlayButton();
        if (_scene?.IsAlpha4Test2Active == true)
        {
            _ipwPipeline?.RequestReset();
            ResetIpwSynchronization();
            SyncAlpha4IpwToPlayer();
            _ipwPresentationPending = true;
            StatusText.Text = "Simülasyon başa alındı; IPW çekirdeği arka planda blank başlangıcına dönüyor.";
        }
        else
        {
            StatusText.Text = "Simülasyon başa alındı.";
        }
    }

    private async void Alpha4IpwStart_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null || _scene is null)
        {
            StatusText.Text = "IPW için önce makineyi ve doğru NX .trjob paketini açın.";
            return;
        }

        SetBusy(true, "Üç yönlü dexel stok ve görünür IPW kuruluyor…");
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await StopIpwPipelineAsync();
            _player.Reset();
            ResetPlaybackGovernor();
            var report = _scene.InitializeAlpha4Test2();
            _ipwVisible = true;
            _gpuScene.SetIpwVisible(true);
            IpwVisibilityButton.IsEnabled = true;
            IpwVisibilityButton.Content = "Stoku gizle";
            StartIpwPipeline();
            ResetIpwSynchronization();
            ApplyPlayerState(forceUiRefresh: true);
            UpdateStockOpacityAvailability();
            ShowAlpha4NcReport(report, null);
            RefreshVisibilityPanel();
            _gpuScene.SynchronizeStaticScene(forceRebuild: true);
            StatusText.Text = "0,15 mm IPW hazır ve program başa alındı. NC’yi oynat düğmesiyle kesimi başlatın.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Alpha4IpwCut_Click(object sender, RoutedEventArgs e)
    {
        if (_scene?.IsAlpha4Test2Active != true)
        {
            StatusText.Text = "Önce Talaş kaldırma panelindeki IPW başlat düğmesine basın.";
            return;
        }

        await Dispatcher.Yield(DispatcherPriority.Background);
        PlayPause_Click(sender, e);
    }

    private async void Alpha4IpwReset_Click(object sender, RoutedEventArgs e)
    {
        if (_scene?.IsAlpha4Test2Active != true)
        {
            StatusText.Text = "Önce Talaş kaldırma panelindeki IPW başlat düğmesine basın.";
            return;
        }

        SetBusy(true, "Triple-dexel IPW başlangıç blankına alınıyor…");
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            _player.Reset();
            ResetPlaybackGovernor();
            _ipwPipeline?.RequestReset();
            ResetIpwSynchronization();
            SyncAlpha4IpwToPlayer();
            _ipwPresentationPending = true;
            StatusText.Text = "IPW sıfırlama arka plan çekirdeğine gönderildi; görüntü blank durumuna güncellenecek.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Alpha4IpwEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        await StopIpwPipelineAsync();
        _scene.EndAlpha4Test2();
        _ipwVisible = true;
        IpwVisibilityButton.IsEnabled = false;
        IpwVisibilityButton.Content = "Stoku gizle";
        UpdateStockOpacityAvailability();
        Alpha4IpwInfoText.Text = "IPW kapalı — orijinal blank görünümü geri getirildi.";
        RefreshVisibilityPanel();
        _gpuScene.ClearIpw();
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        StatusText.Text = "IPW kapatıldı; iş paketindeki başlangıç stoğu geri getirildi.";
    }

    private void ShowAlpha4IpwReport(MachineSceneController.Alpha4IpwTestReport report)
    {
        static double ErrorPercent(double actual, double expected) =>
            expected <= 1e-9 ? 0 : Math.Abs(actual - expected) * 100.0 / expected;

        var removedLine = report.ExpectedRemoved > 0 || report.CutCount > 0
            ? $"Kaldırılan X/Y/Z : {report.Removed.X:0.0} / {report.Removed.Y:0.0} / {report.Removed.Z:0.0} mm³"
            : "Kaldırılan X/Y/Z : —";
        var expectedLine = report.ExpectedRemoved > 0
            ? $"Analitik beklenen   : {report.ExpectedRemoved:0.0} mm³\n" +
              $"Hata X/Y/Z         : %{ErrorPercent(report.Removed.X, report.ExpectedRemoved):0.00} / " +
              $"%{ErrorPercent(report.Removed.Y, report.ExpectedRemoved):0.00} / " +
              $"%{ErrorPercent(report.Removed.Z, report.ExpectedRemoved):0.00}"
            : report.CutCount > 1
                ? "Analitik beklenen   : 0.0 mm³ (aynı kesme tekrarı)"
                : "Analitik beklenen   : —";
        var pass = report.RapidNoCutPassed &&
                   (report.ExpectedRemoved <= 0 ||
                    new[] { report.Removed.X, report.Removed.Y, report.Removed.Z }
                        .All(value => ErrorPercent(value, report.ExpectedRemoved) <= 12.0)) &&
                   (report.CutCount <= 1 ||
                    (Math.Abs(report.Removed.X) < 1e-9 &&
                     Math.Abs(report.Removed.Y) < 1e-9 &&
                     Math.Abs(report.Removed.Z) < 1e-9));

        Alpha4IpwInfoText.Text =
            $"{report.Phase}\n" +
            $"Blank              : {report.Bounds.Size.X:0.###} × {report.Bounds.Size.Y:0.###} × {report.Bounds.Size.Z:0.###} mm\n" +
            $"Izgara X/Y/Z       : {report.SamplesX} / {report.SamplesY} / {report.SamplesZ}\n" +
            $"Pitch X/Y/Z        : {report.PitchX:0.###} / {report.PitchY:0.###} / {report.PitchZ:0.###} mm\n" +
            $"Başlangıç X/Y/Z    : {report.Initial.X:0.0} / {report.Initial.Y:0.0} / {report.Initial.Z:0.0} mm³\n" +
            $"Kalan X/Y/Z        : {report.Current.X:0.0} / {report.Current.Y:0.0} / {report.Current.Z:0.0} mm³\n" +
            $"Hedef altı / oyuk  : {report.GougeVolume:0.0} mm³\n" +
            $"Hedef üstü stok    : {report.ExcessStockVolume:0.0} mm³\n" +
            removedLine + "\n" + expectedLine + "\n" +
            $"G0 stok koruması    : {(report.RapidNoCutPassed ? "GEÇTİ" : "HATA")}\n" +
            $"Render mesh        : {report.MeshTriangles:N0} üçgen\n" +
            $"R33 GPU TEST SONUCU: {(pass ? "GEÇTİ" : "KONTROL ET")}";
    }

    private void ResetIpwSynchronization()
    {
        _ipwPresentationPending = false;
        _lastIpwReportRevision = -1;
        _lastIpwReportDisplay = DateTime.MinValue;
        _ipwPipelineErrorShown = false;
        _ipwPipeline?.Request(0, 0);
    }

    private void SyncAlpha4IpwToPlayer()
    {
        if (_scene?.IsAlpha4Test2Active != true ||
            _program is not { Blocks.Count: > 0 } ||
            _ipwPipeline is null) return;
        var targetBlock = Math.Clamp(_player.DisplayedBlockIndex, 0, _program.Blocks.Count - 1);
        var targetProgress = Math.Clamp(_player.DisplayedBlockProgress, 0, 1);
        _ipwPipeline.Request(targetBlock, targetProgress);
    }

    private int _ipwVisualGeneration;

    private void StartIpwPipeline()
    {
        if (_scene is null || _program is not { Blocks.Count: > 0 }) return;
        _ipwPipeline = new Alpha4IpwBackgroundPipeline(_scene, _program);
        _ipwVisualGeneration = 0;
        _lastIpwReportRevision = -1;
        _lastIpwReportDisplay = DateTime.MinValue;
        _ipwPipelineErrorShown = false;
        ResetPlaybackGovernor();
    }

    private async Task StopIpwPipelineAsync()
    {
        var pipeline = _ipwPipeline;
        _ipwPipeline = null;
        _ipwPresentationPending = false;
        if (pipeline is not null)
            await pipeline.DisposeAsync();
    }

    private void DrainIpwPipeline()
    {
        if (_ipwPipeline is null || _scene is null || _program is not { Blocks.Count: > 0 }) return;

        var ready = _ipwPipeline.GetSnapshot();
        // Wait for one complete stock revision, then assemble whole GPU groups
        // under the UI budget. Never expose half of a regional surface update.
        if (!ready.IsSurfacePrepared && ready.Error is null) return;
        if (ready.VisualGeneration != _ipwVisualGeneration)
        {
            _gpuScene.BeginIpwReplacement();
            _scene.BeginAlpha4IpwVisualReplacement();
            _ipwVisualGeneration = ready.VisualGeneration;
        }

        var publishWatch = Stopwatch.StartNew();
        var publishedVisualChunks = 0;
        while (publishedVisualChunks < MaximumPreparedVisualChunksPerFrame &&
               publishWatch.Elapsed < PreparedVisualPublishBudget)
        {
            var prepared = _ipwPipeline.TakePreparedVisuals(64);
            if (prepared.Count == 0) break;
            _gpuScene.BeginIpwPublication(stage: true);
            try
            {
                _scene.PublishAlpha4IpwVisualChunks(prepared);
                publishedVisualChunks += prepared.Count;
            }
            finally { _gpuScene.EndIpwPublication(); }
        }

        var snapshot = _ipwPipeline.GetSnapshot();
        if (snapshot.IsCaughtUp) _gpuScene.CommitIpwPublication();
        _lastIpwPublishMilliseconds = publishWatch.Elapsed.TotalMilliseconds;
        if (snapshot.Error is not null)
        {
            _player.Pause();
            if (!_ipwPipelineErrorShown)
            {
                _ipwPipelineErrorShown = true;
                StatusText.Text = "IPW arka plan motoru durdu: " + snapshot.Error;
            }
            return;
        }

        if (snapshot.IsCaughtUp && !_player.IsPlaying)
        {
            var drawModelsBefore = _gpuScene.IpwDrawModelCount;
            _gpuScene.CompactIpw();
            var compactDrawModels = _gpuScene.IpwDrawModelCount;
            if (drawModelsBefore > compactDrawModels && compactDrawModels > 0)
            {
                Alpha4IpwInfoText.Text +=
                    $"\nDirectX çizim modeli: {compactDrawModels:N0} (tam kalite toplu tampon)";
                StatusText.Text =
                    $"IPW yüzeyi tam kalitede {compactDrawModels:N0} DirectX tamponuna toplandı; kamera hazır.";
            }
        }

        var now = DateTime.UtcNow;
        if (snapshot.LatestReport is null ||
            snapshot.ReportRevision == _lastIpwReportRevision ||
            (publishedVisualChunks == 0 && now - _lastIpwReportDisplay < TimeSpan.FromMilliseconds(140)))
            return;

        _lastIpwReportRevision = snapshot.ReportRevision;
        _lastIpwReportDisplay = now;
        var blockIndex = Math.Clamp(snapshot.Processed.BlockIndex, 0, _program.Blocks.Count - 1);
        ShowAlpha4NcReport(snapshot.LatestReport, _program.Blocks[blockIndex]);

        var blockLag = Math.Max(0, snapshot.Requested.BlockIndex - snapshot.Processed.BlockIndex);
        var kernelState = snapshot.IsKernelCaughtUp
            ? "EŞİT — 0,15 mm kesme yetişti"
            : _ipwPresentationPending
                ? "KİLİT-ADIM — sıradaki takım/stok karesi hazırlanıyor"
                : $"ARKA PLAN — {blockLag} blok geride";
        var visualState = snapshot.PendingDisplayCuts + snapshot.PreparedVisualChunks + snapshot.DirtySurfaceChunks == 0
            ? "GÜNCEL"
            : $"{snapshot.PendingDisplayCuts} kesme + {snapshot.PreparedVisualChunks} hazır + {snapshot.DirtySurfaceChunks} örülecek";
        Alpha4IpwInfoText.Text +=
            $"\nStok hesabı         : {kernelState}" +
            $"\nGörsel yüzey        : {visualState}" +
            $"\nSon çekirdek işi    : {snapshot.LastKernelMilliseconds:0.0} ms" +
            $"\nSon yüzey paketi    : {snapshot.LastSurfaceMilliseconds:0.0} ms" +
            $"\nBu kare yayınlanan  : {publishedVisualChunks}" +
            $"\nDirectX çizim modeli: {_gpuScene.IpwDrawModelCount}";
    }

    private void ShowAlpha4NcReport(
        MachineSceneController.Alpha4IpwTestReport report,
        GCodeBlock? block)
    {
        var totalRemoved = new TripleDexelVolume(
            Math.Max(0, report.Initial.X - report.Current.X),
            Math.Max(0, report.Initial.Y - report.Current.Y),
            Math.Max(0, report.Initial.Z - report.Current.Z));
        var toolText = block?.Tool is int tool ? ActiveToolText(tool) : "—";
        Alpha4IpwInfoText.Text =
            $"{report.Phase}\n" +
            $"Takım               : {toolText}\n" +
            $"Blank               : {report.Bounds.Size.X:0.###} × {report.Bounds.Size.Y:0.###} × {report.Bounds.Size.Z:0.###} mm\n" +
            $"Izgara X/Y/Z        : {report.SamplesX} / {report.SamplesY} / {report.SamplesZ}\n" +
            $"Kesin stok adımı    : 0,15 mm\n" +
            $"Görsel yüzey adımı  : 0,15 mm (aynı kesin stok)\n" +
            $"Anlık kaldırılan    : {report.Removed.X:0.0} / {report.Removed.Y:0.0} / {report.Removed.Z:0.0} mm³\n" +
            $"Toplam kaldırılan   : {totalRemoved.X:0.0} / {totalRemoved.Y:0.0} / {totalRemoved.Z:0.0} mm³\n" +
            $"Kalan X/Y/Z         : {report.Current.X:0.0} / {report.Current.Y:0.0} / {report.Current.Z:0.0} mm³\n" +
            $"Hedef altı / oyuk   : {report.GougeVolume:0.0} mm³ (kırmızı)\n" +
            $"Hedef üstü stok     : {report.ExcessStockVolume:0.0} mm³\n" +
            $"G0 stok koruması    : {(report.RapidNoCutPassed ? "ETKİN" : "HATA")}\n" +
            $"Render mesh         : {report.MeshTriangles:N0} üçgen";
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        if (_ipwPresentationPending) return;
        if (!_player.Step()) return;
        if (_ipwPipeline is not null && _scene?.IsAlpha4Test2Active == true)
        {
            SyncAlpha4IpwToPlayer();
            _ipwPresentationPending = true;
        }
        else
        {
            ApplyPlayerState();
        }
        UpdatePlayButton();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedText is null) return;
        _player.SpeedFactor = e.NewValue;
        ResetPlaybackGovernor();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (_isBusy) { _lastTick = now; return; }
        var viewportInteractionActive = IsViewportInteractionActive(now);
        SetInteractiveRenderMode(viewportInteractionActive);
        _ipwPipeline?.SetVisualPreparationPaused(viewportInteractionActive);
        // NX prioritizes camera input over display-mesh publication. Exact stock
        // computation keeps running on the background pipeline; only the GPU
        // upload waits until 120 ms after the last wheel/drag message.
        if (!viewportInteractionActive) DrainIpwPipeline();
        if (_scene?.IsAlpha4Test2Active == true)
            _gpuScene.SetIpwTransform(_scene.Alpha4IpwWorldTransform);
        if (_scene is not null && _toolPathVisible)
            _gpuScene.SetOperationPathTransform(_scene.WorkpieceWorldTransform);
        if (_lastTick == default) _lastTick = now;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (_ipwPipeline is { } pipeline)
        {
            var stock = pipeline.GetSnapshot();
            var requested = stock.Requested.BlockIndex + stock.Requested.Progress;
            var processed = stock.Processed.BlockIndex + stock.Processed.Progress;
            _playbackGovernor.ObserveStockBacklog(
                Math.Max(0, requested - processed),
                stock.PendingDisplayCuts);

            if (_ipwPresentationPending)
            {
                if (!stock.IsCaughtUp)
                {
                    UpdateSpeedText();
                    return;
                }

                var commitWork = Stopwatch.StartNew();
                _ipwPresentationPending = false;
                ApplyPlayerState();
                UpdatePlayButton();
                commitWork.Stop();
                _playbackGovernor.ObserveSynchronizedWork(
                    TimeSpan.FromMilliseconds(stock.LastKernelMilliseconds),
                    commitWork.Elapsed + TimeSpan.FromMilliseconds(_lastIpwPublishMilliseconds));
                UpdateSpeedText();
                // The committed stock and pose are synchronized now. Schedule
                // the next bounded step on this tick instead of wasting an
                // extra 16 ms tick between every two IPW frames. The usual
                // pause/collision/end checks below still apply, and the clock
                // never accumulates time spent waiting for the surface.
            }

            // Initial blank/reset surface is also a synchronized presentation
            // frame. Do not start the cutter while that surface is still being
            // reconstructed or uploaded.
            if (_scene?.IsAlpha4Test2Active == true && !stock.IsCaughtUp)
            {
                UpdateSpeedText();
                return;
            }
        }
        if (!_player.IsPlaying)
            return;
        if (!_player.Advance(_playbackGovernor.CreateAdvanceStep(elapsed))) return;

        if (_ipwPipeline is not null && _scene?.IsAlpha4Test2Active == true)
        {
            SyncAlpha4IpwToPlayer();
            _ipwPresentationPending = true;
            UpdateSpeedText();
            return;
        }

        var work = Stopwatch.StartNew();
        ApplyPlayerState();
        UpdatePlayButton();
        work.Stop();
        // Normal machine simulation has no stock workload and must follow the
        // requested NC clock. UI text/list work is never allowed to throttle
        // an IPW-free run; the adaptive governor belongs only to exact IPW.
        if (_scene?.IsAlpha4Test2Active == true)
            _playbackGovernor.ObserveWork(work.Elapsed);
        UpdateSpeedText();
        if (!viewportInteractionActive) DrainIpwPipeline();
    }

    private void SetInteractiveRenderMode(bool active)
    {
        if (_interactiveRenderMode == active) return;
        _interactiveRenderMode = active;
        // Camera input defers mesh publication, not visual quality. Changing
        // MSAA/OIT here recreated targets and changed transparent stock shading.
        GpuViewport.InvalidateRender();
    }

    private void ResetPlaybackGovernor()
    {
        _playbackGovernor.Reset(
            _player.SpeedFactor,
            stockSimulationActive: _scene?.IsAlpha4Test2Active == true);
        UpdateSpeedText();
    }

    private void UpdateSpeedText()
    {
        if (SpeedText is null) return;
        var requested = _player.SpeedFactor;
        var effective = _playbackGovernor.EffectiveSpeed(requested);
        SpeedText.Text = effective >= requested * 0.985
            ? $"{requested:0.0}×"
            : $"{requested:0.0}× → {effective:0.0}×";
        SpeedText.ToolTip = effective >= requested * 0.985
            ? "İstenen oynatma hızı"
            : "İstenen hız → motorun takılmadan sürdürebildiği anlık hız";
    }

    private void ApplyPlayerState(bool forceUiRefresh = false)
    {
        var state = _player.Position;
        _scene?.ApplyAxes(state);
        UpdateNxRotationPivot();

        var index = Math.Clamp(_player.DisplayedBlockIndex, 0, Math.Max(0, (_program?.Blocks.Count ?? 1) - 1));
        var block = _player.CurrentBlock;
        var now = DateTime.UtcNow;
        var refreshUi = forceUiRefresh || !_player.IsPlaying ||
            now - _lastPlaybackUiRefreshUtc >= TimeSpan.FromMilliseconds(90);
        if (refreshUi)
        {
            _lastPlaybackUiRefreshUtc = now;
            AxisXText.Text = $"{state.X:0.000} mm";
            AxisYText.Text = $"{state.Y:0.000} mm";
            AxisZText.Text = $"{state.Z:0.000} mm";
            AxisAText.Text = $"{state.A:0.000}°";
            AxisBText.Text = $"{state.B:0.000}°";
            AxisCText.Text = $"{state.C:0.000}°";
            TimelineSlider.Value = index;
            CurrentBlockText.Text = block is null ? "Blok —" : $"Blok {index + 1}/{_program!.Blocks.Count} · NC satır {block.SourceLine}";
            CurrentToolText.Text = block?.Tool is int tool ? ActiveToolText(tool) : "Takım —";
        }
        var gpuStaticSceneChanged = false;
        if (block?.Tool is int currentTool && _scene is not null)
        {
            // Compare against the tool the scene actually has mounted, not a
            // cached field: seeking straight into the middle of a program must
            // mount that block's modal tool even though no M6 was executed.
            var id = $"T{currentTool:00}";
            var mounted = _scene.ActiveToolId;
            var mountedNumber = new string((mounted ?? string.Empty).Where(char.IsDigit).ToArray());
            if (!int.TryParse(mountedNumber, out var mountedTool) || mountedTool != currentTool)
            {
                _scene.SetActiveTool(id);
                _activeTool = _scene.ActiveToolId;
                gpuStaticSceneChanged = true;
                if (_scene.ActiveToolId is null)
                    StatusText.Text = $"NC satır {block.SourceLine}: {id} iş paketindeki takım listesinde yok; "
                        + "spindle boş gösteriliyor.";
            }
        }

        SyncAlpha4IpwToPlayer();

        if (refreshUi && !_inspectNcDiagnostic && _program is not null && index < _program.Blocks.Count)
        {
            var sourceIndex = Math.Clamp(_program.Blocks[index].SourceLine - 1, 0, _program.SourceLines.Count - 1);
            if (GCodeGrid.SelectedIndex != sourceIndex)
            {
                _syncingSelection = true;
                GCodeGrid.SelectedIndex = sourceIndex;
                GCodeGrid.ScrollIntoView(GCodeGrid.SelectedItem);
                _syncingSelection = false;
            }
            SelectActiveOperation(index);
        }
        _gpuScene.SynchronizeStaticScene(forceRebuild: gpuStaticSceneChanged);
        if (_scene?.IsAlpha4Test2Active == true)
            _gpuScene.SetIpwTransform(_scene.Alpha4IpwWorldTransform);
        if (_scene is not null && _toolPathVisible)
            _gpuScene.SetOperationPathTransform(_scene.WorkpieceWorldTransform);
        CapturePathPresentation();
        EvaluateCollision(block, forceUiRefresh);
        if (block?.ExecutionBlocked == true)
        {
            _player.Pause();
            UpdatePlayButton();
            SetInspectorVisible(true);
            ExecutionTabs.SelectedIndex = 3;
            ExecutionStatusText.Text = StatusText.Text = $"NC satır {block.SourceLine}: " +
                (block.Error ?? "Önceki çözülemeyen komut nedeniyle bu hareket doğrulanamadı.");
        }
    }

    private void EvaluateCollision(GCodeBlock? block, bool forceUiRefresh = false)
    {
        if (CollisionEnabledCheckBox?.IsChecked != true || _scene is null)
        {
            _activeCollisionKeys.Clear();
            if (CollisionStatusText is not null)
            {
                CollisionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(147, 167, 182));
                CollisionStatusText.Text = "Çarpışma kontrolü kapalı.";
            }
            return;
        }

        var clearance = CollisionClearanceSlider?.Value ?? 0;
        var scan = _scene.CheckCollisions(clearance);
        var hitSetChanged = scan.Hits.Count != _activeCollisionKeys.Count;
        if (!hitSetChanged)
            for (var index = 0; index < scan.Hits.Count; index++)
                if (!_activeCollisionKeys.Contains(scan.Hits[index].Key))
                {
                    hitSetChanged = true;
                    break;
                }
        var historyChanged = false;
        if (scan.Hits.Count == 0)
        {
            _activeCollisionKeys.Clear();
        }
        else
        {
            var sourceLineForLog = block?.SourceLine.ToString() ?? "—";
            for (var index = 0; index < scan.Hits.Count; index++)
            {
                var hit = scan.Hits[index];
                if (_activeCollisionKeys.Contains(hit.Key)) continue;
                var eventType = hit.IsClearanceOnly ? "YAKLAŞMA" : "ÇARPIŞMA";
                var eventKey = $"{eventType}|{sourceLineForLog}|{hit.Key}";
                if (!_collisionHistoryEventKeys.Add(eventKey)) continue;
                _collisionHistory.Insert(0, new CollisionLogEntry(
                    eventKey,
                    eventType,
                    sourceLineForLog,
                    hit.First,
                    hit.Second,
                    hit.IsClearanceOnly ? $"{hit.ClearanceMm:0.00} mm" : "0 mm"));
                historyChanged = true;
            }
            _activeCollisionKeys.Clear();
            for (var index = 0; index < scan.Hits.Count; index++)
                _activeCollisionKeys.Add(scan.Hits[index].Key);
            while (_collisionHistory.Count > 250)
            {
                var removed = _collisionHistory[^1];
                _collisionHistory.RemoveAt(_collisionHistory.Count - 1);
                _collisionHistoryEventKeys.Remove(removed.EventKey);
            }
        }

        var now = DateTime.UtcNow;
        if (forceUiRefresh || hitSetChanged || historyChanged ||
            now - _lastCollisionUiRefreshUtc >= TimeSpan.FromMilliseconds(250))
        {
            _lastCollisionUiRefreshUtc = now;
            CollisionDiagnosticsText.Text =
                $"{scan.ProxyCount} gövde · {scan.CandidateCount} aday çift · " +
                $"{scan.BaselineSuppressedCount} başlangıç teması · {scan.ElapsedMilliseconds:0.00} ms" +
                (scan.IsCached ? " · poz değişmedi" : string.Empty);
            if (scan.Hits.Count == 0)
            {
                CollisionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(93, 211, 158));
                CollisionStatusText.Text = $"TEMİZ — {clearance:0.00} mm güvenlik payı korunuyor.";
            }
            else
            {
                var primary = PrimaryCollision(scan.Hits);
                CollisionStatusText.Foreground = primary.IsClearanceOnly
                    ? new SolidColorBrush(Color.FromRgb(255, 193, 7))
                    : new SolidColorBrush(Color.FromRgb(255, 82, 82));
                CollisionStatusText.Text = primary.IsClearanceOnly
                    ? $"YAKLAŞMA — {primary.First} ↔ {primary.Second} ({clearance:0.00} mm pay ihlali)"
                    : $"ÇARPIŞMA — {primary.First} ↔ {primary.Second}";
            }
        }

        if (scan.Hits.Count == 0 ||
            CollisionStopCheckBox?.IsChecked != true ||
            !_player.IsPlaying ||
            !CollisionAlertPolicy.ShouldStopPlayback(scan.Hits.Select(hit => hit.IsClearanceOnly))) return;
        var stopHit = scan.Hits.First(hit => !hit.IsClearanceOnly);
        _player.Pause();
        UpdatePlayButton();
        ExecutionTabs.SelectedIndex = 4;
        SetInspectorVisible(true);
        var sourceLine = block?.SourceLine.ToString() ?? "—";
        StatusText.Text = $"NC satır {sourceLine}: {stopHit.First} ile {stopHit.Second} çarpıştı; simülasyon durduruldu.";
    }

    private static MachineSceneController.CollisionHit PrimaryCollision(
        IReadOnlyList<MachineSceneController.CollisionHit> hits)
    {
        for (var index = 0; index < hits.Count; index++)
            if (!hits[index].IsClearanceOnly) return hits[index];
        return hits[0];
    }

    private void CollisionEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (CollisionStatusText is null) return;
        if (CollisionEnabledCheckBox.IsChecked == true)
            EvaluateCollision(_player.CurrentBlock, forceUiRefresh: true);
        else
            ResetCollisionState(clearHistory: false, "Çarpışma kontrolü kapalı.");
    }

    private void CollisionClearanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CollisionClearanceText is null) return;
        CollisionClearanceText.Text = $"{e.NewValue:0.00} mm";
        if (_scene is not null && CollisionEnabledCheckBox?.IsChecked == true)
            EvaluateCollision(_player.CurrentBlock, forceUiRefresh: true);
    }

    private void CollisionCheckNow_Click(object sender, RoutedEventArgs e) =>
        EvaluateCollision(_player.CurrentBlock, forceUiRefresh: true);

    private void CollisionClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _collisionHistory.Clear();
        _collisionHistoryEventKeys.Clear();
        _activeCollisionKeys.Clear();
        EvaluateCollision(_player.CurrentBlock, forceUiRefresh: true);
    }

    private void ResetCollisionState(bool clearHistory, string status)
    {
        _activeCollisionKeys.Clear();
        if (clearHistory)
        {
            _collisionHistory.Clear();
            _collisionHistoryEventKeys.Clear();
        }
        _lastCollisionUiRefreshUtc = DateTime.MinValue;
        if (CollisionStatusText is null) return;
        CollisionStatusText.Foreground = new SolidColorBrush(Color.FromRgb(147, 167, 182));
        CollisionStatusText.Text = status;
        CollisionDiagnosticsText.Text = "—";
    }

    private string ActiveToolText(int toolNumber)
    {
        var id = $"T{toolNumber:00}";
        var tool = _job?.Tools.FirstOrDefault(x =>
            x.Id.Trim().TrimStart('T', 't', '0').Equals(toolNumber.ToString(), StringComparison.OrdinalIgnoreCase));
        return tool is null
            ? id
            : $"{id} · {tool.Name} · D{tool.Diameter:0.###}";
    }

    private void TimelineSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_program is null) return;
        _player.Pause();
        _player.Seek((int)Math.Round(TimelineSlider.Value), completed: true);
        ResetPlaybackGovernor();
        QueueSynchronizedIpwPresentationOrApply();
        UpdatePlayButton();
    }

    private void GCodeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _program is null || GCodeGrid.SelectedItem is not GCodeSourceLine sourceLine) return;
        if (!sourceLine.BlockIndex.HasValue)
        {
            StatusText.Text = $"NC satır {sourceLine.SourceLine}: yorum veya boş kaynak satırı; simülasyon konumu değişmedi.";
            return;
        }
        _player.Pause();
        _player.Seek(sourceLine.BlockIndex.Value, completed: true);
        ResetPlaybackGovernor();
        QueueSynchronizedIpwPresentationOrApply();
        UpdatePlayButton();
    }

    private void QueueSynchronizedIpwPresentationOrApply()
    {
        if (_ipwPipeline is not null && _scene?.IsAlpha4Test2Active == true)
        {
            SyncAlpha4IpwToPlayer();
            _ipwPresentationPending = true;
            return;
        }

        ApplyPlayerState();
    }

    private GCodeSimulationContext? BuildGCodeSimulationContext()
    {
        if (_job is null || _scene is null) return null;
        var placement = _scene.LastWorkpiecePlacement;
        CoordinateFrame PlaceFrame(CoordinateFrame frame, string source)
        {
            var sourceOrigin = ToPoint(frame.Origin);
            var worldOrigin = ToVector(placement.Transform(sourceOrigin));
            Vector3 TransformAxis(Vector3 axis)
            {
                var end = placement.Transform(ToPoint(frame.Origin + axis));
                var value = ToVector(end) - worldOrigin;
                return value.LengthSquared() > 1e-10f ? Vector3.Normalize(value) : axis;
            }
            return new CoordinateFrame(
                frame.Label,
                worldOrigin,
                TransformAxis(frame.XAxis),
                TransformAxis(frame.YAxis),
                TransformAxis(frame.ZAxis),
                source);
        }
        var workFrame = PlaceFrame(_job.Mcs, "trjob:cam-mcs->canonical-machine");
        var controllerFrame = _job.HasExplicitControllerWorkFrame
            ? PlaceFrame(_job.ControllerWorkFrame, "trjob:controller-work-frame->canonical-machine")
            : workFrame;
        var controllerToVisual = CoordinateTransforms.BuildPlacement(controllerFrame, workFrame);

        var gaugeLengths = new Dictionary<int, double>();
        var toolRadii = new Dictionary<int, double>();
        foreach (var tool in _scene.EffectiveTools)
        {
            var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var number)) continue;
            var mesh = !string.IsNullOrWhiteSpace(tool.ModelPath) && File.Exists(_job.Resolve(tool.ModelPath))
                ? StlMeshReader.Load(_job.Resolve(tool.ModelPath))
                : ParametricToolMeshBuilder.Build(tool);
            var mountPoint = tool.MountPoint ?? new Vector3(mesh.Bounds.Max.X, 0, 0);
            var tipPoint = tool.TipPoint ?? new Vector3(mesh.Bounds.Min.X, 0, 0);
            gaugeLengths[number] = Vector3.Distance(mountPoint, tipPoint);
            if (tool.Diameter > 0) toolRadii[number] = tool.Diameter * 0.5;
        }
        // Some SINUMERIK posts call tools by name (T="END_MILL_10")
        // instead of a T number, so the parser needs the name to number map.
        var toolNumbersByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _scene.EffectiveTools)
        {
            var digits = new string(tool.Id.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var number)) continue;
            if (!string.IsNullOrWhiteSpace(tool.Name)) toolNumbersByName[tool.Name.Trim()] = number;
            if (!string.IsNullOrWhiteSpace(tool.Id)) toolNumbersByName[tool.Id.Trim()] = number;
        }
        return new GCodeSimulationContext(
            workFrame, gaugeLengths, controllerFrame, controllerToVisual, toolNumbersByName, toolRadii);

        static Point3D ToPoint(Vector3 value) => new(value.X, value.Y, value.Z);
        static Vector3 ToVector(Point3D value) => new((float)value.X, (float)value.Y, (float)value.Z);
    }

    private async void ToolHolderLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null || _scene is null)
        {
            MessageBox.Show(this, "Önce NX .trjob sim paketini açın.", "Takım kütüphanesi",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ToolHolderLibraryWindow(
            _scene.EffectiveTools,
            ToolHolderLibrary.Create(_job.Tools),
            _scene.ActiveToolId)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedToolId)) return;

        try
        {
            _player.Pause();
            UpdatePlayButton();
            await StopIpwPipelineAsync();
            _scene.EndAlpha4Test2();
            _gpuScene.ClearIpw();
            _ipwVisible = true;
            IpwVisibilityButton.IsEnabled = false;
            IpwVisibilityButton.Content = "Stoku gizle";

            JobTool effective;
            if (dialog.ResetRequested)
                effective = _scene.ResetHolderOverride(dialog.SelectedToolId);
            else
                effective = _scene.ApplyHolderPreset(dialog.SelectedToolId, dialog.SelectedPreset!);

            ToolGrid.ItemsSource = _scene.EffectiveTools;
            _activeTool = _scene.ActiveToolId;
            _gpuScene.SynchronizeStaticScene(forceRebuild: true);
            if (!string.IsNullOrWhiteSpace(_ncPath) && File.Exists(_ncPath))
                await LoadNcAsync(_ncPath);
            else
            {
                _simulationContext = BuildGCodeSimulationContext();
                ApplyPlayerState(forceUiRefresh: true);
            }
            StatusText.Text = dialog.ResetRequested
                ? $"{effective.Id} için NX .trjob tutucusu geri yüklendi."
                : $"{effective.Id} için {effective.Holder} uygulandı; kesici ölçüleri korundu, tutucu çarpışma gövdesi yenilendi.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void WorkpieceOffset_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null || _scene is null)
        {
            MessageBox.Show(this, "Önce makine ve NX .trjob sim paketini açın.", "CSYS konumu",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new WorkpieceOffsetWindow(_scene.WorkpieceCsysOffset) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _player.Pause();
            UpdatePlayButton();
            await StopIpwPipelineAsync();
            _scene.EndAlpha4Test2();
            _gpuScene.ClearIpw();
            _ipwVisible = true;
            IpwVisibilityButton.IsEnabled = false;
            IpwVisibilityButton.Content = "Stoku gizle";
            _scene.SetWorkpieceCsysOffset(dialog.Offset);
            _gpuScene.SynchronizeStaticScene(forceRebuild: false);
            _gpuScene.SetOperationPathTransform(_scene.WorkpieceWorldTransform);
            UpdateCsysOffsetText();
            UpdateNxRotationPivot();

            if (!string.IsNullOrWhiteSpace(_ncPath) && File.Exists(_ncPath))
                await LoadNcAsync(_ncPath);
            else
            {
                _simulationContext = BuildGCodeSimulationContext();
                ApplyPlayerState(forceUiRefresh: true);
            }
            StatusText.Text = dialog.Offset.LengthSquared() <= 1e-8f
                ? "Kullanıcı CSYS düzeltmesi sıfırlandı; NX machineMountCsys doğrudan PART_MOUNT_JCT noktasına bağlı."
                : $"İş kurulumu seçilen CSYS eksenlerinde X {dialog.Offset.X:+0.###;-0.###;0}, Y {dialog.Offset.Y:+0.###;-0.###;0}, Z {dialog.Offset.Z:+0.###;-0.###;0} mm taşındı; NC yeni çerçeveyle başa alındı.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void UpdateCsysOffsetText()
    {
        var offset = _scene?.WorkpieceCsysOffset ?? Vector3.Zero;
        CsysOffsetText.Text = $"X {offset.X:+0.###;-0.###;0} · Y {offset.Y:+0.###;-0.###;0} · Z {offset.Z:+0.###;-0.###;0} mm";
        CsysOffsetText.Foreground = offset.LengthSquared() <= 1e-8f ? (Brush)FindResource("ForegroundPrimary") : Brushes.Gold;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        try
        {
            foreach (var path in paths.Where(IsMachinePackage)) await LoadMachineAsync(path);
            foreach (var path in paths.Where(IsJobPackage)) await LoadJobAsync(path);
            foreach (var path in paths.Where(path => !IsMachinePackage(path) && !IsJobPackage(path))) await LoadNcAsync(path);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// The selected machineMountCsys is the placement contract and it is never
    /// overridden.  A part can legitimately be clamped high, low or off-centre;
    /// that is exactly what the CSYS is for, and MANUSSim works the same way -
    /// it exports geometry and the selected CSYS in one common space and maps
    /// that CSYS onto the machine mount.  So nothing here moves the job.  The
    /// only thing reported is whether the package itself is internally
    /// consistent: an export written without WCS normalization puts its CSYS
    /// values in a different space than its STL vertices.  The NX2412 raw export
    /// proves it - mount declared Z-100 with the exported fixture base at STL
    /// Z0, and MCS Z94.5 with the exported stock at Z134.5..195.5, both off by
    /// the same 100 mm.  That is an exporter defect and it is named as such.
    /// </summary>
    private string ReportMountFrameConsistency(JobPackage job)
    {
        SeatWarningText.Visibility = Visibility.Collapsed;
        if (_scene?.LastTableFrame is null) return string.Empty;

        var notes = new List<string>();
        if (!job.MountFrameSharesStlSpace)
        {
            var normalizeFailed = job.MountAxisConvention.Contains("normalize-failed", StringComparison.OrdinalIgnoreCase);
            notes.Add(normalizeFailed
                ? $"Aktarıcıdaki WCS normalizasyonu çalıştı fakat hata verdi; paket bunu kendisi bildiriyor (axisConvention: {job.MountAxisConvention}). "
                    + "CSYS değerleri STL uzayına taşınamamış."
                : $"Bu paket, CSYS'leri STL uzayına dönüştürmeden yazılmış (axisConvention: {job.MountAxisConvention}). "
                    + "Aktarıcının eski sürümüyle alınmış; bu bir aktarım kusurudur, seçtiğiniz CSYS ile ilgili değildir.");
        }

        if (!McsMatchesExportedStock(job, out var mcsAlong, out var stockLow, out var stockHigh))
            notes.Add($"Bildirilen MCS tabla ekseninde {mcsAlong:0.000} mm'de, aktarılan stok gövdesi ise "
                + $"{stockLow:0.000} … {stockHigh:0.000} mm arasında. İkisi aynı uzayda değil.");

        var lowest = LowestWorkGroupPoint(job);
        if (!double.IsPositiveInfinity(lowest) && Math.Abs(lowest) > 0.01)
            notes.Add($"Bilgi: seçtiğiniz CSYS'ye göre iş grubunun en alt noktası tabla yüzeyine göre {lowest:+0.000;-0.000} mm'de. "
                + "Parçayı yüksekten veya kaçık bağladıysanız bu beklenen bir sonuçtur ve uygulama buna dokunmaz.");

        if (notes.Count == 0) return string.Empty;
        if (!job.MountFrameSharesStlSpace)
            notes.Add("Kalıcı çözüm: aktarıcıyı WCS normalizasyonu içeren sürümle yeniden derleyip işi tekrar alın.");
        SeatWarningText.Text = string.Join(" ", notes);
        SeatWarningText.Visibility = Visibility.Visible;
        return " " + notes[0];
    }

    /// <summary>
    /// Is the declared MCS inside the exported stock along the table normal?
    /// The cheapest objective test for "the CSYS values and the STL geometry
    /// were written in the same space".
    /// </summary>
    private bool McsMatchesExportedStock(JobPackage job, out double mcsAlong, out double low, out double high)
    {
        mcsAlong = 0; low = 0; high = 0;
        if (_scene?.LastTableFrame is null) return true;
        var table = _scene.LastTableFrame;
        var placement = _scene.LastWorkpiecePlacement;
        double Along(Point3D world) =>
            (world.X - table.Origin.X) * table.ZAxis.X
            + (world.Y - table.Origin.Y) * table.ZAxis.Y
            + (world.Z - table.Origin.Z) * table.ZAxis.Z;

        var asset = job.Models.FirstOrDefault(x => x.Role.Contains("stock", StringComparison.OrdinalIgnoreCase))
            ?? job.Models.FirstOrDefault(x => x.Role.Contains("part", StringComparison.OrdinalIgnoreCase));
        if (asset is null) return true;
        var file = job.Resolve(asset.Path);
        if (!File.Exists(file)) return true;
        var bounds = StlMeshReader.Load(file).Bounds;
        low = double.PositiveInfinity;
        high = double.NegativeInfinity;
        foreach (var corner in BoundsCorners(bounds))
        {
            var along = Along(placement.Transform(new Point3D(corner.X, corner.Y, corner.Z)));
            low = Math.Min(low, along);
            high = Math.Max(high, along);
        }
        mcsAlong = Along(placement.Transform(new Point3D(job.Mcs.Origin.X, job.Mcs.Origin.Y, job.Mcs.Origin.Z)));
        return mcsAlong >= low - 0.01 && mcsAlong <= high + 0.01;
    }

    private double LowestWorkGroupPoint(JobPackage job)
    {
        if (_scene?.LastTableFrame is null) return double.PositiveInfinity;
        var table = _scene.LastTableFrame;
        var placement = _scene.LastWorkpiecePlacement;
        var lowest = double.PositiveInfinity;
        foreach (var asset in job.Models.Where(x => x.Path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase)))
        {
            var file = job.Resolve(asset.Path);
            if (!File.Exists(file)) continue;
            foreach (var corner in BoundsCorners(StlMeshReader.Load(file).Bounds))
            {
                var world = placement.Transform(new Point3D(corner.X, corner.Y, corner.Z));
                lowest = Math.Min(lowest,
                    (world.X - table.Origin.X) * table.ZAxis.X
                    + (world.Y - table.Origin.Y) * table.ZAxis.Y
                    + (world.Z - table.Origin.Z) * table.ZAxis.Z);
            }
        }
        return lowest;
    }

    private static IEnumerable<Vector3> BoundsCorners(Bounds3 bounds)
    {
        yield return new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
        yield return new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
        yield return new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
        yield return new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
        yield return new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
        yield return new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
        yield return new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
        yield return new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Max.Z);
    }

    private double PlacementError(JobPackage job)
    {
        if (_scene?.LastTableFrame is null) return double.PositiveInfinity;
        var origin = job.MachineMount.Origin;
        var placed = _scene.LastWorkpiecePlacement.Transform(new Point3D(origin.X, origin.Y, origin.Z));
        var target = _scene.LastTableFrame.Origin;
        var delta = new Vector3((float)placed.X, (float)placed.Y, (float)placed.Z) - target;
        return delta.Length();
    }

    private static CoordinateFrame FindTableFrame(MachinePackage machine)
    {
        return MachineCoordinateResolver.ResolveTableFrame(machine);
    }

    private static string FrameText(CoordinateFrame frame) =>
        $"{frame.Label}\nO [{frame.Origin.X:0.###}, {frame.Origin.Y:0.###}, {frame.Origin.Z:0.###}]\n" +
        $"X [{frame.XAxis.X:0.###}, {frame.XAxis.Y:0.###}, {frame.XAxis.Z:0.###}]\n" +
        $"Y [{frame.YAxis.X:0.###}, {frame.YAxis.Y:0.###}, {frame.YAxis.Z:0.###}]\n" +
        $"Z [{frame.ZAxis.X:0.###}, {frame.ZAxis.Y:0.###}, {frame.ZAxis.Z:0.###}]";

    private void RefreshVisibilityPanel()
    {
        if (_scene is null || ComponentVisibilityPanel is null) return;
        _refreshingVisibility = true;
        try
        {
            ComponentVisibilityPanel.Children.Clear();
            foreach (var item in _scene.GetVisibilityItems())
            {
                var checkBox = new CheckBox
                {
                    Content = item.Label,
                    IsChecked = item.IsVisible,
                    Tag = item.Key,
                    Margin = new Thickness(0, 2, 0, 2),
                    Foreground = item.Group == "workpiece"
                        ? (Brush)FindResource("ForegroundPrimary")
                        : item.Group == "tool" ? (Brush)FindResource("Primary") : (Brush)FindResource("ForegroundPrimary"),
                    ToolTip = "Görünürlüğü aç/kapat"
                };
                checkBox.Checked += ComponentVisibility_Changed;
                checkBox.Unchecked += ComponentVisibility_Changed;
                ComponentVisibilityPanel.Children.Add(checkBox);
            }
        }
        finally
        {
            _refreshingVisibility = false;
        }
    }

    private void ComponentVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingVisibility || _scene is null || sender is not CheckBox checkBox || checkBox.Tag is not string key) return;
        _scene.SetComponentVisible(key, checkBox.IsChecked == true);
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        StatusText.Text = $"Komponent görünürlüğü değişti: {checkBox.Content}";
    }

    private void IpwVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_scene?.IsAlpha4Test2Active != true)
        {
            StatusText.Text = "IPW kapalı; gösterilecek kesilmiş stok yok.";
            return;
        }
        _ipwVisible = !_ipwVisible;
        _gpuScene.SetIpwVisible(_ipwVisible);
        _scene.SetAlpha4IpwDisplayVisible(_ipwVisible);
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        RefreshVisibilityPanel();
        IpwVisibilityButton.Content = _ipwVisible ? "Stoku gizle" : "Stoku göster";
        StatusText.Text = _ipwVisible
            ? "IPW görünür."
            : "IPW gizlendi; stok hesabı etkinse arka planda aynı konumdan devam eder.";
    }

    private void OperationsPanel_Click(object sender, RoutedEventArgs e)
    {
        SetInspectorVisible(true);
        ExecutionTabs.SelectedIndex = 1;
        if (_toolPathOperations.Count == 0)
        {
            StatusText.Text = "Operasyon listesi için önce NX işiyle eşleşen NC programını açın.";
            return;
        }
        OperationGrid.Focus();
        StatusText.Text = "Operasyonlar sekmesi açıldı. Tek seçim veya Ctrl/Shift ile çoklu seçim yapabilirsiniz.";
    }

    private async void OperationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingOperationSelection) return;
        var selected = SelectedToolPathOperations();
        OperationSummaryText.Text = selected.Count == 0
            ? $"{_toolPathOperations.Count} operasyon bulundu; göstermek istediğinizi seçin."
            : $"{selected.Count} operasyon seçildi: {string.Join(", ", selected.Take(3).Select(operation => operation.Name))}" +
              (selected.Count > 3 ? "…" : string.Empty);
        if (!_toolPathVisible || !_operationScopedPath) return;
        _toolPathSelectionDebounce?.Cancel();
        _toolPathSelectionDebounce?.Dispose();
        var debounce = new CancellationTokenSource();
        _toolPathSelectionDebounce = debounce;
        try
        {
            await Task.Delay(120, debounce.Token);
            if (!debounce.IsCancellationRequested)
                await ShowSelectedOperationPathAsync();
        }
        catch (OperationCanceledException)
        {
            // Ctrl/Shift selection raises several events; only the settled
            // selection should spend CPU building geometry.
        }
    }

    private async void OperationGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OperationGrid.SelectedItem is not GCodeOperationRange) return;
        OperationGoToSelected();
        _operationScopedPath = true;
        await ShowSelectedOperationPathAsync();
    }

    private async void OperationShowPath_Click(object sender, RoutedEventArgs e)
    {
        _operationScopedPath = true;
        await ShowSelectedOperationPathAsync();
    }

    private async void ShowOperationLinksCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_toolPathVisible)
            await ShowSelectedOperationPathAsync();
    }

    private void OperationGoTo_Click(object sender, RoutedEventArgs e) => OperationGoToSelected();

    private void OperationHidePath_Click(object sender, RoutedEventArgs e)
    {
        _toolPathBuildRevision++;
        _toolPathVisible = false;
        SetPathChecked(false);
        _gpuScene.SetOperationPathVisible(false);
        OperationPathStatusText.Text = "Operasyon yolu gizlendi.";
        StatusText.Text = "Operasyon yolu gizlendi.";
    }

    private void OperationGoToSelected()
    {
        if (_program is null || OperationGrid.SelectedItem is not GCodeOperationRange operation)
        {
            return;
        }
        _player.Pause();
        _player.Seek(operation.StartBlockIndex, completed: false);
        ResetPlaybackGovernor();
        QueueSynchronizedIpwPresentationOrApply();
        UpdatePlayButton();
        ExecutionTabs.SelectedIndex = 0;
        StatusText.Text = $"{operation.Name} operasyonunun başlangıcına gidildi (NC satır {operation.StartSourceLine}).";
    }

    private async Task ShowSelectedOperationPathAsync()
    {
        if (_program is null || _simulationContext is null || _machine is null || _scene is null)
        {
            StatusText.Text = "Operasyon yolu için önce makine, iş ve NC programını açın.";
            return;
        }

        var operations = _operationScopedPath ? SelectedToolPathOperations() : _toolPathOperations;
        if (operations.Count == 0)
        {
            var activeIndex = GCodeOperationCatalog.FindOperationIndex(
                _toolPathOperations,
                Math.Max(0, _player.DisplayedBlockIndex));
            if (activeIndex < 0)
            {
                StatusText.Text = "Gösterilecek operasyon bulunamadı.";
                return;
            }
            _syncingOperationSelection = true;
            OperationGrid.SelectedIndex = activeIndex;
            OperationGrid.ScrollIntoView(OperationGrid.SelectedItem);
            _syncingOperationSelection = false;
            operations = SelectedToolPathOperations();
        }

        var program = _program;
        var machine = _machine;
        var gauges = _simulationContext.ToolGaugeLengths;
        var inversePlacement = _scene.LastWorkpiecePlacement;
        if (!inversePlacement.HasInverse)
        {
            StatusText.Text = "Operasyon yolu için iş parçası yerleştirme matrisi çözülemedi.";
            return;
        }
        inversePlacement.Invert();
        var includeLinks = ShowOperationLinksCheckBox.IsChecked == true;
        var revision = ++_toolPathBuildRevision;
        OperationPathStatusText.Text = $"{operations.Count} operasyonun NX yolu hazırlanıyor…";
        try
        {
            var data = await Task.Run(() => GCodeOperationPathBuilder.Build(
                program,
                gauges,
                operations,
                (state, gauge) => ToWorkpieceLocalToolTip(machine, inversePlacement, state, gauge),
                includeLinks));
            if (revision != _toolPathBuildRevision || !ReferenceEquals(program, _program)) return;
            if (data.CuttingSegmentCount == 0)
            {
                _gpuScene.ClearOperationPath();
                _toolPathVisible = false;
                OperationPathStatusText.Text = "Seçilen aralıkta çizilecek gerçek kesme hareketi bulunamadı.";
                StatusText.Text = OperationPathStatusText.Text;
                return;
            }
            _gpuScene.SetOperationPathTransform(_scene.WorkpieceWorldTransform);
            _gpuScene.SetOperationPath(data);
            _gpuScene.SetOperationPathVisible(true);
            _toolPathVisible = true;
            SetPathChecked(true);
            UpdatePathAtCommittedCursor();
            var names = string.Join(", ", operations.Take(3).Select(operation => operation.Name));
            if (operations.Count > 3) names += "…";
            OperationPathStatusText.Text =
                $"NX operasyon yolu: {names} · {data.CuttingSegmentCount:N0} kesme" +
                (includeLinks ? $" · {data.LinkingSegmentCount:N0} yerel bağlantı" : string.Empty) +
                " · mavi kesme, kehribar G0.";
            SetPathModeStatus();
        }
        catch (Exception ex)
        {
            if (revision == _toolPathBuildRevision)
                ShowError(ex);
        }
    }

    private IReadOnlyList<GCodeOperationRange> SelectedToolPathOperations() =>
        OperationGrid.SelectedItems
            .Cast<GCodeOperationRange>()
            .OrderBy(operation => operation.StartBlockIndex)
            .ToArray();

    private void CancelToolPathSelectionDebounce()
    {
        _toolPathSelectionDebounce?.Cancel();
        _toolPathSelectionDebounce?.Dispose();
        _toolPathSelectionDebounce = null;
    }

    private void SelectActiveOperation(int blockIndex)
    {
        if (FollowActiveOperationCheckBox.IsChecked != true || _toolPathOperations.Count == 0)
            return;
        var operationIndex = GCodeOperationCatalog.FindOperationIndex(_toolPathOperations, blockIndex);
        if (operationIndex < 0 || OperationGrid.SelectedItems.Count == 1 && OperationGrid.SelectedIndex == operationIndex)
            return;
        _syncingOperationSelection = true;
        OperationGrid.SelectedItems.Clear();
        OperationGrid.SelectedIndex = operationIndex;
        OperationGrid.ScrollIntoView(OperationGrid.SelectedItem);
        _syncingOperationSelection = false;
        if (_toolPathVisible && _operationScopedPath)
            _ = ShowSelectedOperationPathAsync();
    }

    private static Vector3 ToWorkpieceLocalToolTip(
        MachinePackage machine,
        Matrix3D inversePlacement,
        AxisState state,
        double gauge)
    {
        var tipWorld = MachineToolCoordinates.For(machine).TipWorld(state, gauge);
        var neutralTip = MachineCoordinateResolver.InverseTransformWorkpiecePoint(
            machine,
            tipWorld,
            state.B,
            state.C);
        var local = inversePlacement.Transform(new Point3D(neutralTip.X, neutralTip.Y, neutralTip.Z));
        return new Vector3((float)local.X, (float)local.Y, (float)local.Z);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F8) return;
        _gpuScene.SnapNearestOrthographicView();
        MarkViewportInteraction();
        StatusText.Text = "F8 SNAP VIEW: görünüm en yakın ana düzleme dikleştirildi.";
        e.Handled = true;
    }

    private void FocusJob_Click(object sender, RoutedEventArgs e)
    {
        if (_scene is null || _job is null)
        {
            StatusText.Text = "Önce NX .trjob iş paketini açın.";
            return;
        }
        _scene.FocusJob();
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        _gpuScene.ZoomExtents();
        RefreshVisibilityPanel();
        StatusText.Text = "Yalnız parça, blank, fikstür ve aktif takım gösteriliyor.";
    }

    private void FocusMachiningArea_Click(object sender, RoutedEventArgs e)
    {
        if (_scene is null || _job is null)
        {
            StatusText.Text = "Önce makine ile NX .trjob iş paketini açın.";
            return;
        }
        _scene.FocusMachiningArea();
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        _gpuScene.ZoomExtents();
        RefreshVisibilityPanel();
        StatusText.Text = "İşleme alanı görünümü: tabla eksenleri, spindle, parça, blank, fikstür ve aktif takım.";
    }

    private void HideCabin_Click(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        _scene.HideExteriorComponents();
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        _gpuScene.ZoomExtents();
        RefreshVisibilityPanel();
        StatusText.Text = "Dış kabin ve kapı gizlendi.";
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        if (_scene is null) return;
        _scene.ShowAllComponents();
        _gpuScene.SynchronizeStaticScene(forceRebuild: true);
        _gpuScene.ZoomExtents();
        RefreshVisibilityPanel();
        StatusText.Text = "Tüm makine ve iş komponentleri gösteriliyor.";
    }

    private void MachineOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MachineOpacityText is null) return;
        MachineOpacityText.Text = $"{e.NewValue:0}%";
        _scene?.SetMachineOpacity(e.NewValue / 100.0);
        _gpuScene?.SynchronizeStaticScene();
    }

    private void StockOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StockOpacityText is null || _restoringStockOpacity) return;
        if (_scene?.IsAlpha4Test2Active == true)
        {
            _restoringStockOpacity = true;
            StockOpacitySlider.Value = _acceptedStockOpacityPercent;
            _restoringStockOpacity = false;
            StockOpacityText.Text = $"{_acceptedStockOpacityPercent:0}%";
            StatusText.Text = "Stok opaklığı NX'teki gibi yalnız IPW başlatılmadan önce değiştirilebilir.";
            return;
        }
        _acceptedStockOpacityPercent = e.NewValue;
        StockOpacityText.Text = $"{e.NewValue:0}%";
        _scene?.SetStockOpacity(e.NewValue / 100.0);
        _gpuScene?.SynchronizeStaticScene();
    }

    private static string RoleLabel(string role)
    {
        if (role.Contains("stock", StringComparison.OrdinalIgnoreCase) || role.Contains("blank", StringComparison.OrdinalIgnoreCase)) return "Blank/Stock";
        if (role.Contains("fixture", StringComparison.OrdinalIgnoreCase)) return "Fikstür";
        if (role.Contains("part", StringComparison.OrdinalIgnoreCase) || role.Contains("model", StringComparison.OrdinalIgnoreCase)) return "Parça/Model";
        return role;
    }

    private void UpdateRuntime()
    {
        if (_scene is null) return;
        RuntimeText.Text = $"Makine: {_scene.MachineMeshCount} mesh\nİş: {_scene.JobMeshCount} mesh\nÜçgen: {_scene.TriangleCount:N0}";
    }

    private void UpdatePlayButton()
    {
        UpdateStockOpacityAvailability();
        if (_player.IsPlaying)
        {
            PlayButton.Content = "Duraklat";
            PlayButton.Tag = "Pause24";
            PlayButton.Background = (Brush)FindResource("WarningBrush");
        }
        else
        {
            PlayButton.Content = "Oynat";
            PlayButton.Tag = "Play24";
            PlayButton.Background = (Brush)FindResource("Primary");
        }
    }

    private void UpdateStockOpacityAvailability()
    {
        if (StockOpacitySlider is null) return;
        var available = _scene?.IsAlpha4Test2Active != true;
        StockOpacitySlider.IsEnabled = available;
        if (IpwVisibilityButton is not null)
            IpwVisibilityButton.IsEnabled = !available;
        StockOpacitySlider.ToolTip = available
            ? "Stok opaklığını IPW başlamadan önce ayarlayın."
            : "IPW çalışırken opaklık NX'teki gibi kilitlidir; değiştirmek için IPW kapat'a basın.";
        RefreshWorkspacePresentation();
    }

    private void SetBusy(bool busy, string message = "")
    {
        _isBusy = busy;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) BusyText.Text = message;
        else RefreshWorkspacePresentation();
    }

    private void ShowError(Exception exception)
    {
        SetBusy(false);
        StatusText.Text = "Hata: " + exception.Message;
        MessageBox.Show(this, exception.Message, "TRMachinist", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool IsMachinePackage(string path) => Path.GetExtension(path).Equals(".trmac", StringComparison.OrdinalIgnoreCase);
    private static bool IsJobPackage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".shopdocv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".trjob", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ResolveStartupPaths()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Select(path => ResolveStartupPath(path, applicationDirectory))
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();
    }

    private static string? ResolveStartupPath(string path, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return Path.GetFullPath(path);
        if (Path.IsPathRooted(path)) return null;
        var packagedPath = Path.GetFullPath(Path.Combine(applicationDirectory, path));
        return File.Exists(packagedPath) ? packagedPath : null;
    }

}


