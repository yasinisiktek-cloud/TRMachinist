using System.Globalization;
using System.Numerics;
using System.Windows;

namespace TRMachinist.Simulator;

public partial class WorkpieceOffsetWindow : Window
{
    public Vector3 Offset { get; private set; }

    public WorkpieceOffsetWindow(Vector3 currentOffset)
    {
        InitializeComponent();
        OffsetXTextBox.Text = currentOffset.X.ToString("0.###", CultureInfo.CurrentCulture);
        OffsetYTextBox.Text = currentOffset.Y.ToString("0.###", CultureInfo.CurrentCulture);
        OffsetZTextBox.Text = currentOffset.Z.ToString("0.###", CultureInfo.CurrentCulture);
        OffsetXTextBox.SelectAll();
        OffsetXTextBox.Focus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(OffsetXTextBox.Text, out var x) ||
            !TryRead(OffsetYTextBox.Text, out var y) ||
            !TryRead(OffsetZTextBox.Text, out var z))
        {
            MessageBox.Show(this, "X, Y ve Z alanlarına geçerli milimetre değerleri girin.",
                "Geçersiz ofset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) > 5000)
        {
            MessageBox.Show(this, "Ofset değerleri ±5000 mm aralığında olmalı.",
                "Geçersiz ofset", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Offset = new Vector3((float)x, (float)y, (float)z);
        DialogResult = true;
    }

    private void Zero_Click(object sender, RoutedEventArgs e)
    {
        Offset = Vector3.Zero;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryRead(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
