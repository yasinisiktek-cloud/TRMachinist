using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TRMachinist.Core;

namespace TRMachinist.Simulator;

public partial class MainWindow
{
    private bool _syncingPathControls;
    private bool _operationScopedPath;
    private bool _isBusy;
    private bool _inspectNcDiagnostic;
    private SimulationCursor _presentedPathCursor = new(-1, 0);
    private Vector3 _presentedPathTip;

    private async void ToolPathMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingPathControls || _gpuScene is null || _program is null) return;
        if (ReferenceEquals(sender, ProgressiveToolPathCheckBox))
        {
            _syncingPathControls = true;
            ToolPathVisibleCheckBox.IsChecked = true;
            _syncingPathControls = false;
        }
        if (ToolPathVisibleCheckBox.IsChecked != true)
        {
            OperationHidePath_Click(sender, e);
            return;
        }
        // A mode toggle retains a prepared path; it never rebuilds the whole
        // NC stream on each timer tick.
        if (_toolPathVisible) UpdatePathAtCommittedCursor();
        else
        {
            _operationScopedPath = false;
            await ShowSelectedOperationPathAsync();
        }
        SetPathModeStatus();
    }

    private void SetPathChecked(bool value)
    {
        _syncingPathControls = true;
        ToolPathVisibleCheckBox.IsChecked = value;
        _syncingPathControls = false;
    }

    private void ResetPathAndDiagnostics()
    {
        _toolPathBuildRevision++;
        CancelToolPathSelectionDebounce();
        _toolPathVisible = false;
        _gpuScene.ClearOperationPath();
        SetPathChecked(false);
        ToolPathVisibleCheckBox.IsEnabled = ProgressiveToolPathCheckBox.IsEnabled = false;
        _operationScopedPath = false;
        _presentedPathCursor = new(-1, 0);
        _inspectNcDiagnostic = false;
        NcDiagnosticsList.ItemsSource = null;
        NcDiagnosticsEmptyText.Visibility = Visibility.Visible;
        NcDiagnosticsEmptyText.Text = "NC programı yüklendiğinde açıklamalar burada görünür.";
    }

    private void CapturePathPresentation()
    {
        if (_machine is null || _scene is null || _simulationContext is null) return;
        var inverse = _scene.LastWorkpiecePlacement;
        if (!inverse.HasInverse) return;
        inverse.Invert();
        var gauge = _player.CurrentBlock?.Tool is int tool && _simulationContext.ToolGaugeLengths.TryGetValue(tool, out var value)
            ? value : 0;
        _presentedPathTip = ToWorkpieceLocalToolTip(_machine, inverse, _player.Position, gauge);
        _presentedPathCursor = new(_player.DisplayedBlockIndex, _player.DisplayedBlockProgress);
        UpdatePathAtCommittedCursor();
    }

    private void UpdatePathAtCommittedCursor()
    {
        if (!_toolPathVisible) return;
        // The player may already have requested a future stock frame. A mode
        // toggle uses the last presented pose, so the line cannot outrun IPW.
        _gpuScene.UpdateOperationPathProgress(ProgressiveToolPathCheckBox.IsChecked == true,
            _presentedPathCursor.BlockIndex, _presentedPathCursor.Progress, _presentedPathTip);
    }

    private void SetPathModeStatus()
    {
        if (!_toolPathVisible) return;
        StatusText.Text = ProgressiveToolPathCheckBox.IsChecked == true
            ? "Takım yolu ilerledikçe çiziliyor · mavi kesme, kehribar G0 bağlantısı."
            : (_operationScopedPath ? "Seçili operasyonların" : "Programın") + " takım yolunun tamamı görünür · mavi kesme, kehribar G0 bağlantısı.";
    }

    private sealed record NcDiagnostic(int SourceLine, string Kind, string Message, string Raw)
    {
        public string Heading => $"{Kind} · NC satır {SourceLine}";
    }

    private void RefreshNcDiagnostics()
    {
        var items = _program?.SourceLines.SelectMany(line =>
        {
            var rows = new List<NcDiagnostic>(2);
            if (!string.IsNullOrWhiteSpace(line.Warning)) rows.Add(new(line.SourceLine, "Uyarı", line.Warning, line.Raw));
            if (!string.IsNullOrWhiteSpace(line.Error)) rows.Add(new(line.SourceLine, "Hata", line.Error, line.Raw));
            return rows;
        }).ToArray() ?? Array.Empty<NcDiagnostic>();
        NcDiagnosticsList.ItemsSource = items;
        NcDiagnosticsEmptyText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NcDiagnosticsEmptyText.Text = "NC programında ayrıştırma uyarısı veya hatası yok.";
        ProgramSummaryText.ToolTip = items.Length == 0 ? "NC durumu — ayrıntılar için tıklayın."
            : string.Join("\n", items.Take(8).Select(i => $"{i.Heading}: {i.Message}")) + "\nTüm açıklamalar için tıklayın.";
    }

    private void ShowNcDiagnostics_Click(object sender, MouseButtonEventArgs e)
    {
        SetInspectorVisible(true);
        ExecutionTabs.SelectedIndex = 3;
    }

    private void NcDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NcDiagnostic diagnostic } || _program is null) return;
        _player.Pause();
        UpdatePlayButton();
        _inspectNcDiagnostic = true;
        SetInspectorVisible(true);
        ExecutionTabs.SelectedIndex = 0;
        _syncingSelection = true;
        try
        {
            GCodeGrid.SelectedItem = _program.SourceLines.FirstOrDefault(l => l.SourceLine == diagnostic.SourceLine);
            GCodeGrid.ScrollIntoView(GCodeGrid.SelectedItem);
        }
        finally { _syncingSelection = false; }
        StatusText.Text = $"{diagnostic.Heading}: {diagnostic.Message}";
    }
}
