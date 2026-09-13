using System.Numerics;
using System.Reflection;
using TRMachinist.Core;

internal static class ProfileQualityTests
{
    internal static JobTool Tool(string subtype, double diameter, double corner = 0) => new(
        "T1", "GEOMETRY_ONLY", "Mill", diameter, corner, 75, "-", null, subtype,
        25, diameter, 118, 45, 0, "", "parametric", [], []);

    internal static void CurvedNoses()
    {
        foreach (var radius in new[] { .5, 2.0, 6.0, 20.0 })
        foreach (var inner in new[] { 0.0, 2.0, 24.0 })
        {
            var tool = Tool(inner == 0 ? "MillBall" : "Mill5", 2 * (radius + inner), radius) with { TipAngle = 0 };
            var profile = ToolCuttingProfileFactory.Create(tool);
            double end = 0, lastRadius = inner;
            foreach (var layer in profile.Layers)
            {
                if (Math.Abs(layer.AxialOffset - end) > 1e-8 || Math.Abs((layer.StartRadius ?? layer.Radius) - lastRadius) > 1e-8)
                    throw new Exception("Cutting profile has an axial or radial step.");
                if (layer.IsTapered)
                    for (int i = 0; i <= 100; i++)
                    {
                        double t = i / 100.0, z = layer.AxialOffset + t * layer.Length;
                        double r = layer.StartRadius!.Value + t * (layer.Radius - layer.StartRadius.Value);
                        double sagitta = radius - Math.Sqrt((r - inner) * (r - inner) + (z - radius) * (z - radius));
                        if (sagitta < -1e-7 || sagitta > .015001)
                            throw new Exception($"Tool profile exceeded 0.015 mm inscribed chord error: {sagitta}");
                    }
                end = layer.AxialOffset + layer.Length; lastRadius = layer.Radius;
            }
        }
        // Independent circle envelope at actual grid rays for all six principal
        // directions. A stationary cutter uses the optimized sweep path too.
        var ball = ToolCuttingProfileFactory.Create(Tool("MillBall", 6, 3) with { TipAngle = 0 });
        foreach (var axis in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
        {
            var stock = new TripleDexelStock(new Bounds3(new(-4.5f), new(4.5f)), .15);
            stock.ApplyLayeredCutterMove(Vector3.Zero, Vector3.Zero, axis, axis, ball.Layers, false, 1);
            for (int z = -24; z <= 24; z += 3)
            for (int y = -24; y <= 24; y += 3)
            for (int x = -24; x <= 24; x += 3)
            {
                var point = new Vector3(x, y, z) * .15f;
                double h = Vector3.Dot(point, axis), r = (point - axis * (float)h).Length();
                if (h < .03 || h > 4.4) continue;
                double expectedRadius = h < 3 ? Math.Sqrt(9 - (h - 3) * (h - 3)) : 3;
                if (Math.Abs(r - expectedRadius) < .08) continue;
                bool occupied = Contains(stock, point);
                if (occupied != (r > expectedRadius)) throw new Exception($"Curved cutter occupancy error at {point}, axis {axis}.");
            }
        }
    }

    internal static void IncrementalComparison()
    {
        var random = new Random(323);
        var stock = new TripleDexelStock(new Bounds3(new(-6), new(6)), .15);
        var target = stock.CreateZTarget(Box(new(-4.5f), new(4.5f)));
        for (int n = 0; n < 90; n++)
        {
            if (n == 40) stock.Reset();
            if (n == 60) target = stock.CreateZTarget(Box(new(-3), new(3)));
            Compare();
            var a = new Vector3((float)(random.NextDouble() * 8 - 4), (float)(random.NextDouble() * 8 - 4), (float)(random.NextDouble() * 8 - 4));
            var b = a + new Vector3(1, 1, n % 3 == 0 ? 0 : 1);
            var axis = n % 2 == 0 ? Vector3.UnitZ : Vector3.Normalize(new Vector3(1, 2, 3));
            stock.ApplyLayeredCutterMove(a, b, axis, axis,
                new[] { new CutterCylinderLayer(0, 1.2, 3) { StartRadius = n % 3 == 0 ? 0 : null } }, n % 7 == 0, 1);
            if (n % 4 != 0) Compare(); // Also test several cuts before a refresh.
        }
        Compare();
        void Compare()
        {
            var full = stock.CompareWithTarget(target); var incremental = stock.CompareWithTargetIncremental(target);
            if (Math.Abs(full.MissingTargetVolume - incremental.MissingTargetVolume) > 1e-7 ||
                Math.Abs(full.ExcessStockVolume - incremental.ExcessStockVolume) > 1e-7 ||
                Math.Abs(full.TargetVolume - incremental.TargetVolume) > 1e-7)
                throw new Exception($"Target report is stale: {full} / {incremental}");
        }
    }

    internal static void InclinedSweep()
    {
        // Near-lateral translation formerly cancelled large cap coefficients
        // and admitted a point more than a millimetre outside a pointed cone.
        var flags=BindingFlags.Static|BindingFlags.NonPublic;
        var kernel=typeof(TripleDexelStock).GetMethod("PrepareCylinderRay",flags)!.Invoke(null,new object?[] {
            new Vector3(50000,70000,-30000),new Vector3(.3535534f,-.3535534f,.8660254f),6.0,6.0,Vector3.UnitZ,0.0 });
        object?[] parameters={new Vector3(49995.19f,70001.19f,0),kernel,new Vector3(-14.924392f,0,6.092858f),0.0,0.0};
        bool hit=(bool)typeof(TripleDexelStock).GetMethod("SweptFrustumLineInterval",flags)!.Invoke(null,parameters)!;
        if(hit && -29997.44>=(double)parameters[3]! && -29997.44<=(double)parameters[4]!)
            throw new Exception("Near-lateral cap cancellation invented a cutting interval.");
        var axis = Vector3.Normalize(new Vector3(1, 2, 3));
        var move = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ)) * 4;
        var start = -move * .5f - axis * 1.5f; var end = start + move;
        var stock = new TripleDexelStock(new Bounds3(new(-4.5f), new(4.5f)), .15);
        stock.ApplyLayeredCutterMove(start, end, axis, axis,
            new[] { new CutterCylinderLayer(0, 2, 3) { StartRadius = 0 } }, false, 4);
        for(int z=-27;z<=27;z+=2)
        for(int y=-27;y<=27;y+=2)
        for(int x=-27;x<=27;x+=2)
        {
            var point = new Vector3(x,y,z) * .15f;
            var delta = point - start;
            double h = Vector3.Dot(delta, axis);
            var radial = delta - axis * (float)h;
            var t = Math.Clamp(Vector3.Dot(radial, move) / move.LengthSquared(), 0, 1);
            double distance = (radial - move * t).Length(), radius = h * 2 / 3;
            if(Math.Abs(h)<.02 || Math.Abs(h-3)<.02 || Math.Abs(distance-radius)<.04) continue;
            bool removed = h>=0 && h<=3 && distance<=radius;
            if(Contains(stock, point) == removed)
                throw new Exception($"Inclined continuous sweep mismatch at {point}.");
        }
    }

    internal static void TargetClassification()
    {
        var stock = new TripleDexelStock(new Bounds3(new(-6), new(6)), .15);
        // Closed upper/lower slabs with an actual air space between them.
        var target = stock.CreateZTarget(Merge(Box(new(-4.5f, -4.5f, -4.5f), new(4.5f, 4.5f, -1.5f)),
            Box(new(-4.5f, -4.5f, 1.5f), new(4.5f, 4.5f, 4.5f))));
        var check = typeof(ZDexelTarget).GetMethod("IsInterior", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Interior(Vector3 p) => (bool)check.Invoke(target, new object[] { p, .1125 })!;
        foreach (var p in new[] { Vector3.Zero, new Vector3(0, 0, -1.5f), new Vector3(0, 0, 1.5f), new Vector3(4.5f, 0, 3) })
            if (Interior(p)) throw new Exception("Valid target cavity or boundary was painted as a gouge.");
        foreach (var p in new[] { new Vector3(0, 0, -3), new Vector3(0, 0, 3), new Vector3(4, 0, 3), new Vector3(0, 4, 3) })
            if (!Interior(p)) throw new Exception("Real target penetration was hidden by its orientation.");
        foreach(var axis in new[] {Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ})
        {
            var cutStock = new TripleDexelStock(new Bounds3(new(-4.5f),new(4.5f)),.15);
            var fullTarget = cutStock.CreateZTarget(Box(new(-4.5f),new(4.5f)));
            cutStock.ApplyLayeredCutterMove(axis*-5,axis*-5,axis,axis,new[]{new CutterCylinderLayer(0,1.2,3)},false,2);
            if(!cutStock.BuildAllSurfaceChunks(fullTarget).SelectMany(c=>c.Meshes).Any(m=>m.Tag<0 && m.Mesh.TriangleCount>0))
                throw new Exception($"A real gouge lost its red surface on axis {axis}.");
        }
    }

    private static bool Contains(TripleDexelStock stock, Vector3 point)
    {
        var method = typeof(TripleDexelStock).GetMethod("ContainsStock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method != null) return (bool)method.Invoke(stock, new object[] { point })!;
        var field = typeof(TripleDexelStock).GetField("_zField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stock)!;
        int x = (int)Math.Round((point.X + 4.5) / .15), y = (int)Math.Round((point.Y + 4.5) / .15);
        var ray = field.GetType().GetMethod("Ray")!.Invoke(field, new object[] { x, y })!;
        return (bool)ray.GetType().GetMethod("Contains")!.Invoke(ray, new object[] { (double)point.Z })!;
    }

    private static TriangleMeshData Box(Vector3 min, Vector3 max)
    {
        var v = new[] { new Vector3(min.X,min.Y,min.Z), new Vector3(max.X,min.Y,min.Z), new Vector3(max.X,max.Y,min.Z), new Vector3(min.X,max.Y,min.Z),
            new Vector3(min.X,min.Y,max.Z), new Vector3(max.X,min.Y,max.Z), new Vector3(max.X,max.Y,max.Z), new Vector3(min.X,max.Y,max.Z) };
        return new() { Positions = v.SelectMany(p => new[] { p.X,p.Y,p.Z }).ToArray(), Normals = new float[24],
            Indices = new[] {0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7}, Bounds = new(min,max) };
    }
    private static TriangleMeshData Merge(TriangleMeshData a, TriangleMeshData b) => new() {
        Positions = a.Positions.Concat(b.Positions).ToArray(), Normals = a.Normals.Concat(b.Normals).ToArray(),
        Indices = a.Indices.Concat(b.Indices.Select(i => i + a.Positions.Length / 3)).ToArray(), Bounds = new(Vector3.Min(a.Bounds.Min,b.Bounds.Min),Vector3.Max(a.Bounds.Max,b.Bounds.Max)) };
}
