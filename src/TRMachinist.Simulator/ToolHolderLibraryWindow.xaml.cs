using System.Windows;
using System.Windows.Controls;
using TRMachinist.Core;

namespace TRMachinist.Simulator;

public partial class ToolHolderLibraryWindow : Window
{
    private sealed record ToolRow(JobTool Tool)
    {
        public string Display => $"{Tool.Id}  ·  {Tool.Name}\n{HolderText}";
        private string HolderText => string.IsNullOrWhiteSpace(Tool.HolderLibraryReference)
            ? Tool.Holder
            : Tool.HolderLibraryReference;
    }

    private sealed record PresetRow(ToolHolderPreset Preset)
    {
        public string Name => Preset.Name;
        public string Interface => Preset.Interface;
        public string LengthText => $"{Preset.AxialLength:0.##} mm";
        public string DiameterText => $"{Preset.MaximumDiameter:0.##}";
    }

    public string? SelectedToolId { get; private set; }
    public ToolHolderPreset? SelectedPreset { get; private set; }
    public bool ResetRequested { get; private set; }

    public ToolHolderLibraryWindow(
        IReadOnlyList<JobTool> tools,
        IReadOnlyList<ToolHolderPreset> presets,
        string? activeToolId)
    {
        InitializeComponent();
        var toolRows = tools.Select(tool => new ToolRow(tool)).ToArray();
        var presetRows = presets.Select(preset => new PresetRow(preset)).ToArray();
        ToolList.ItemsSource = toolRows;
        PresetGrid.ItemsSource = presetRows;
        ToolList.SelectedItem = toolRows.FirstOrDefault(row =>
            string.Equals(row.Tool.Id, activeToolId, StringComparison.OrdinalIgnoreCase)) ?? toolRows.FirstOrDefault();
        PresetGrid.SelectedIndex = presetRows.Length > 0 ? 0 : -1;
        RefreshDetails();
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDetails();
    private void PresetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDetails();

    private void RefreshDetails()
    {
        if (ToolList.SelectedItem is ToolRow tool)
        {
            ToolDetailsText.Text =
                $"Kesici Ø {tool.Tool.Diameter:0.###} mm\n" +
                $"Takım boyu {tool.Tool.Length:0.###} mm · Sap Ø {tool.Tool.ShankDiameter:0.###} mm\n" +
                $"Mevcut tutucu: {tool.Tool.Holder}\n" +
                $"TOOLINS: {tool.Tool.ToolInsertion:0.###} mm";
        }
        else ToolDetailsText.Text = "Takım seçin.";

        if (PresetGrid.SelectedItem is PresetRow preset)
        {
            var sections = string.Join("  |  ", preset.Preset.Sections.Select((section, index) =>
                $"{index + 1}: Ø{section.Diameter:0.##} × {section.Length:0.##} mm"));
            PresetDetailsText.Text =
                $"Kaynak: {preset.Preset.Source}\n" +
                $"TOOLINS: {preset.Preset.ToolInsertion:0.###} mm\n" + sections;
        }
        else PresetDetailsText.Text = "Tutucu seçin.";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolRow tool || PresetGrid.SelectedItem is not PresetRow preset)
        {
            MessageBox.Show(this, "Bir takım ve bir tutucu seçin.", "Eksik seçim",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedToolId = tool.Tool.Id;
        SelectedPreset = preset.Preset;
        ResetRequested = false;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolRow tool)
        {
            MessageBox.Show(this, "Önce bir takım seçin.", "Eksik seçim",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedToolId = tool.Tool.Id;
        SelectedPreset = null;
        ResetRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
