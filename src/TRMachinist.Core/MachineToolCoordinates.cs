using System.Numerics;
using System.Runtime.CompilerServices;

namespace TRMachinist.Core;

/// <summary>Maps physical XYZ slide readings to the canonical spindle gauge point.
/// A machine's assembly origin and its control's zero need not coincide.</summary>
public sealed class MachineToolCoordinates
{
    private static readonly ConditionalWeakTable<MachinePackage, MachineToolCoordinates> Cache = new();
    public static MachineToolCoordinates For(MachinePackage machine) => Cache.GetValue(machine, Create);
    private readonly Matrix4x4 slidesToWorld;
    private readonly Matrix4x4 worldToSlides;
    public Vector3 ToolAxis { get; }
    private readonly bool identity;

    private MachineToolCoordinates(Matrix4x4 matrix, Vector3 toolAxis)
    {
        slidesToWorld = matrix;
        if (!Matrix4x4.Invert(matrix, out worldToSlides))
            throw new InvalidDataException("Makinenin XYZ kızak yönleri terslenemiyor.");
        ToolAxis = toolAxis;
        identity = matrix == Matrix4x4.Identity && toolAxis == Vector3.UnitZ;
    }

    public Vector3 TipWorld(AxisState state, double gauge)
    {
        // Keep the established identity-map arithmetic exact for existing packages.
        if (identity) return new Vector3((float)state.X, (float)state.Y, (float)(state.Z-gauge));
        return Vector3.Transform(new Vector3((float)state.X,(float)state.Y,(float)state.Z),slidesToWorld)-ToolAxis*(float)gauge;
    }
    public AxisState SlidesForTip(Vector3 tip, AxisState rotary, double gauge)
    {
        if (identity) return rotary with { X=tip.X,Y=tip.Y,Z=tip.Z+gauge };
        var value=Vector3.Transform(tip+ToolAxis*(float)gauge,worldToSlides);
        return rotary with { X=value.X,Y=value.Y,Z=value.Z };
    }
    private static MachineToolCoordinates Create(MachinePackage machine)
    {
        var mount=machine.Junctions.FirstOrDefault(j=>j.Name.Equals("POCKET_JCT",StringComparison.OrdinalIgnoreCase))
                  ?? machine.Junctions.FirstOrDefault(j=>j.Name.Equals("S",StringComparison.OrdinalIgnoreCase));
        // Parser-only fixtures and old non-geometric packages preserve their existing identity contract.
        if (mount is null) return new MachineToolCoordinates(Matrix4x4.Identity,Vector3.UnitZ);
        var ancestors=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? owner=mount.Owner;
        while (!string.IsNullOrEmpty(owner) && ancestors.Add(owner))
            owner=machine.Components.FirstOrDefault(c=>c.Name.Equals(owner,StringComparison.OrdinalIgnoreCase))?.Parent;
        var axes="XYZ".Select(a=>machine.Axes.FirstOrDefault(x=>!x.IsRotary && x.Name.Equals(a.ToString(),StringComparison.OrdinalIgnoreCase)
                                                            && ancestors.Contains(x.Component))).ToArray();
        if (axes.Any(a=>a is null))
            throw new InvalidDataException("Takım bağlama zincirinde üç doğrusal XYZ kızak çözülemedi; bu topoloji için takım dönüşümü desteklenmiyor.");
        var x=Vector3.Normalize(axes[0]!.Vector);var y=Vector3.Normalize(axes[1]!.Vector);var z=Vector3.Normalize(axes[2]!.Vector);
        var origin=mount.Origin-x*(float)axes[0]!.InitialPosition-y*(float)axes[1]!.InitialPosition-z*(float)axes[2]!.InitialPosition;
        // Export rounding near zero is not a new physical offset.
        origin=new Vector3(Snap(origin.X),Snap(origin.Y),Snap(origin.Z));
        var axis=Vector3.Normalize(new Vector3(mount.Orientation.M11,mount.Orientation.M12,mount.Orientation.M13));
        axis=new Vector3(Snap(axis.X),Snap(axis.Y),Snap(axis.Z));
        return new MachineToolCoordinates(new Matrix4x4(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,origin.X,origin.Y,origin.Z,1),axis);
    }
    private static float Snap(float value)=>Math.Abs(value)<1e-5f?0:value;
}
