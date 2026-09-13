namespace TRMachinist.Core;

/// <summary>
/// One cylindrical or conical section of a rotational cutting envelope.
/// Offset is measured from the programmed tool tip toward the holder. The
/// original three-argument cylinder contract remains valid; StartRadius adds
/// a continuous frustum. Shanks and holders are never cutting geometry.
/// </summary>
public readonly record struct CutterCylinderLayer(
    double AxialOffset,
    double Radius,
    double Length)
{
    // A null start radius preserves the original cylindrical section contract.
    // Otherwise this is a finite frustum, from StartRadius to Radius over Length.
    public double? StartRadius { get; init; }
    public bool IsTapered => StartRadius is double start && start != Radius;
    public double MaximumRadius => Math.Max(Radius, StartRadius ?? Radius);
    public bool IsValid => double.IsFinite(AxialOffset) && double.IsFinite(Length) && Length > 0 &&
        double.IsFinite(Radius) && Radius >= 0 && MaximumRadius > 0 &&
        (StartRadius is null || double.IsFinite(StartRadius.Value) && StartRadius.Value >= 0);
    public double MotionStep(double pitch) => IsTapered ? pitch * 1.5 :
        Math.Max(pitch * 1.5, Math.Min(Radius * 0.5, 5.0));
}

public sealed record ToolCuttingProfile(
    string Family,
    bool IsSupported,
    IReadOnlyList<CutterCylinderLayer> Layers,
    string Status)
{
    public static ToolCuttingProfile Unsupported(string family, string reason) =>
        new(family, false, Array.Empty<CutterCylinderLayer>(), reason);
}

/// <summary>
/// Clean-room cutter classification driven only by the NX tool-builder fields
/// carried in TRJOB.  Unknown profiles fail closed: they do not alter stock.
/// </summary>
public static class ToolCuttingProfileFactory
{
    private const double CurvedProfileChordError = 0.015;

    public static ToolCuttingProfile Create(JobTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var radius = tool.Diameter * 0.5;
        var flute = tool.FluteLength > 0 ? tool.FluteLength : tool.Length;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(flute) || flute <= 0)
            return ToolCuttingProfile.Unsupported("GEOMETRİ YOK", "Pozitif kesici çapı ve ağız boyu yok; stok korunuyor.");

        var kind = $"{tool.Subtype} {tool.Type}".ToUpperInvariant();

        // A tap or thread mill does not sweep a solid full-diameter cylinder:
        // its teeth create a thread form.  Until that form is modeled, cutting
        // it as a cylinder is a guaranteed gouge and must therefore fail closed.
        if (kind.Contains("THREAD") || kind.Contains("TAP"))
            return ToolCuttingProfile.Unsupported("DİŞ PROFİLİ", "Diş/tarak geometrisi henüz doğrulanmadı; stok korunuyor.");

        if (kind.Contains("CHAMFER"))
        {
            // A chamfer mill is not the full-diameter flat cylinder selected by
            // the generic MILL fallback. Derive its cone from package geometry.
            if (kind.Contains("FRONTBACK") || !double.IsFinite(tool.Radius) || tool.Radius < 0 || tool.Radius > 0.001 || !double.IsFinite(tool.TaperAngle) ||
                tool.TaperAngle <= 0 || tool.TaperAngle >= 90)
                return ToolCuttingProfile.Unsupported("PAH FREZESİ", "Konik kesici açısı veya profil verisi yetersiz; stok korunuyor.");
            var slope = Math.Tan(tool.TaperAngle * Math.PI / 180.0);
            var coneLength = Math.Min(radius / slope, flute);
            var sections = new List<CutterCylinderLayer>
            {
                new(0, Math.Min(radius, coneLength * slope), coneLength) { StartRadius = 0 }
            };
            if (flute > coneLength + 1e-9)
                sections.Add(new(coneLength, radius, flute - coneLength));
            return new("KONİK PAH FREZESİ", true, sections, "Paket geometrisinden konik kesici profil.");
        }

        if (kind.Contains("BALL") || kind.Contains("SPHER"))
            return Curved("KÜRE UÇLU FREZE", flute, radius, height =>
            {
                var centered = height - radius;
                return Math.Sqrt(Math.Max(0, (radius * radius) - (centered * centered)));
            }, Math.Min(radius, flute), circleRadius: radius);

        if (kind.Contains("REAM") || tool.TipAngle >= 179.0)
            return Flat("DÜZ UÇLU RAYBA", radius, flute);

        if (kind.Contains("DRILL") || kind.Contains("SPOT"))
        {
            if (tool.TipAngle <= 1 || tool.TipAngle >= 179)
                return ToolCuttingProfile.Unsupported("MATKAP", "Matkap uç açısı geçersiz; stok korunuyor.");
            var halfAngle = tool.TipAngle * Math.PI / 360.0;
            var coneHeight = radius / Math.Max(1e-6, Math.Tan(halfAngle));
            coneHeight = Math.Min(coneHeight, flute);
            var sections = new List<CutterCylinderLayer> {
                new(0, Math.Min(radius, coneHeight * Math.Tan(halfAngle)), coneHeight) { StartRadius = 0 }
            };
            if (flute > coneHeight + 1e-9) sections.Add(new(coneHeight, radius, flute - coneHeight));
            return new("KONİK UÇLU MATKAP", true, sections, "Paket geometrisinden konik matkap ucu.");
        }

        if (kind.Contains("MILL"))
        {
            var corner = tool.Radius;
            if (double.IsFinite(corner) && corner > 0.001 && corner < radius - 0.001)
            {
                var inner = radius - corner;
                return Curved("KÖŞE RADYÜSLÜ FREZE", flute, radius, height =>
                {
                    var centered = height - corner;
                    return inner + Math.Sqrt(Math.Max(0, (corner * corner) - (centered * centered)));
                }, Math.Min(corner, flute), inner, corner);
            }
            return Flat("DÜZ PARMAK/YÜZEY FREZE", radius, flute);
        }

        return ToolCuttingProfile.Unsupported("BİLİNMEYEN", $"Desteklenmeyen NX takım tipi: {tool.Subtype}/{tool.Type}; stok korunuyor.");
    }

    private static ToolCuttingProfile Flat(string family, double radius, double flute) =>
        new(family, true, new[] { new CutterCylinderLayer(0, radius, flute) }, "Kesici profil doğrulandı.");

    private static ToolCuttingProfile Curved(
        string family,
        double flute,
        double maximumRadius,
        Func<double, double> radiusAtHeight,
        double profileHeight,
        double initialRadius = 0,
        double circleRadius = 1)
    {
        // Inscribed straight chords join continuously; cylindrical stair steps
        // introduced nose errors far larger than the authoritative stock pitch.
        var angle = Math.Acos(Math.Clamp(1 - profileHeight / circleRadius, -1, 1));
        var stepAngle = 2 * Math.Acos(Math.Clamp(1 - CurvedProfileChordError / circleRadius, -1, 1));
        var count = Math.Max(1, (int)Math.Ceiling(angle / stepAngle));
        var layers = new List<CutterCylinderLayer>(count + 1);
        double previousHeight = 0, previousRadius = initialRadius;
        for (var step = 1; step <= count; step++)
        {
            var height = step == count ? profileHeight : circleRadius * (1 - Math.Cos(angle * step / count));
            var radius = Math.Clamp(radiusAtHeight(height), 0, maximumRadius);
            layers.Add(new(previousHeight, radius, height - previousHeight) { StartRadius = previousRadius });
            previousHeight = height; previousRadius = radius;
        }
        if (flute > profileHeight + 1e-9)
            layers.Add(new(profileHeight, maximumRadius, flute - profileHeight));
        return new(family, true, layers, "Sürekli kesici profil; yay kiriş hatası en fazla 0,015 mm.");
    }
}
