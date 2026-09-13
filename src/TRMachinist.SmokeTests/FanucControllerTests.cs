using System.Numerics;
using TRMachinist.Core;

internal static class FanucControllerTests
{
    private static void Check(bool value,string reason) { if(!value)throw new Exception(reason); }
    private static void Near(double a,double b,string reason,double tolerance=.001) => Check(Math.Abs(a-b)<tolerance,$"{reason}: {a} != {b}");
    private static void Near(Vector3 a,Vector3 b,string reason) => Check(Vector3.Distance(a,b)<.002,$"{reason}: {a} != {b}");
    private static MachinePackage Machine(bool mount=false) => new() {
        SourcePath="test", CacheRoot="", PackageSha256="", Schema="", MachineName="unrelated name",ControllerFamily="Fanuc",RootComponent="base",
        ControllerCommands=new HashSet<string>{"G254","G234"}, Geometry=Array.Empty<GeometryAsset>(),CollidableComponents=new HashSet<string>(),
        Components=new[]{new MachineComponent("base",null,null,null,"",Matrix4x4.Identity),new MachineComponent("head","base",null,null,"",Matrix4x4.Identity)},
        Axes=new[]{new AxisDefinition("X","Linear","head","",Vector3.UnitX,10,false,-2000,2000,1000),new AxisDefinition("Y","Linear","head","",Vector3.UnitY,20,false,-2000,2000,1000),new AxisDefinition("Z","Linear","head","",Vector3.UnitZ,300,false,-2000,2000,1000),
            new AxisDefinition("B","Rotary","B","b-pivot",-Vector3.UnitY,0,false,-180,180,100),new AxisDefinition("C","Rotary","C","c-pivot",-Vector3.UnitZ,0,false,-360,360,100)},
        Junctions=(mount?new[]{new JunctionDefinition("S","head",new Vector3(110,220,330),new Matrix4x4(0,0,1,0,0,1,0,0,-1,0,0,0,0,0,0,1))}:Array.Empty<JunctionDefinition>())
            .Concat(new[]{new JunctionDefinition("b-pivot","B",new Vector3(0,0,50),Matrix4x4.Identity),new JunctionDefinition("c-pivot","C",Vector3.Zero,Matrix4x4.Identity)}).ToArray()
    };
    private static GCodeSimulationContext Context() => new(CoordinateFrame.Identity() with { Origin=new Vector3(30,40,80) },new Dictionary<int,double>{{2,120},{11,90}},ToolRadii:new Dictionary<int,double>{{2,5},{11,3}});
    private static GCodeProgram Parse(params string[] lines)=>GCodeParser.Parse("test",lines,Machine(),Context());
    public static void CommentsAndRegisters()
    {
        var p=Parse("T2 M06 (UGT0202_1006 X999 G91 (T777))","T11","G43 H2 G0 X1 Y2 Z3","G49","G0 Z3","G43 H11 Z3","G43 H0 Z3","G43.4 H2");
        Check(p.Blocks[0].Tool==2 && p.Blocks[1].Tool==2,"Inline comment and preselection must not replace T2");
        Near(p.Blocks[2].End.Z,203,"G43 H2 uses register length");Near(p.Blocks[4].End.Z,83,"G49 cancels length compensation");
        Near(p.Blocks[5].End.Z,173,"H register is independent of active T");Near(p.Blocks[6].End.Z,83,"H0 clears length value");
        Check(p.Blocks[^1].Error?.Contains("G43.4")==true,"Fractional G code cannot round to G43");
        Check(Parse("G234").ErrorCount==1,"G234 requires H in same block");
        Check(Parse("G254","G234 H2").ErrorCount==1,"DWO and TCPC conflict is explicit");
        Check(Parse("G43 H9 X1").ErrorCount==1,"Missing register is explicit");
        var bad=Parse("G0 X5","G43 H9 Z-10","G1 X20 F100");
        var player=new SimulationPlayer();player.Load(bad);player.Play();player.Advance(TimeSpan.FromSeconds(100));
        Check(!player.IsPlaying && player.CurrentBlock!.SourceLine==2 && player.DisplayedBlockProgress==0,"Unresolved controller command stops before motion");
        player.Step();Check(player.CurrentBlock!.SourceLine==2,"Step cannot execute unknown H");
        player.Seek(2,true);Check(player.CurrentBlock!.SourceLine==2 && player.DisplayedBlockProgress==0,"Seek cannot cut past unresolved controller input");
        Check(Parse("G68 X0 Y0 R30").ErrorCount==1,"Unimplemented transform is not silently ignored");
    }
    public static void SlidesAndGauge()
    {
        var machine=Machine(true);var map=MachineToolCoordinates.For(machine);var random=new Random(7421);
        for(int i=0;i<500;i++) {
            var s=new AxisState(random.NextDouble()*100,random.NextDouble()*100,random.NextDouble()*300,0,0,0);double gauge=12+random.NextDouble()*150;
            var expected=new Vector3((float)s.X+100,(float)s.Y+200,(float)s.Z+30-(float)gauge);
            Near(map.TipWorld(s,gauge),expected,"Slide origin differs from assembly origin");
            var back=map.SlidesForTip(expected,s,gauge);Near(back.X,s.X,"X roundtrip");Near(back.Y,s.Y,"Y roundtrip");Near(back.Z,s.Z,"Z roundtrip");
        }
        var p=GCodeParser.Parse("translated",new[]{"T2 M6","G43 H2 G0 X1 Y2 Z3"},machine,Context());
        Near(p.Blocks[^1].End.X,-69,"Canonical origin maps back to physical X");
        Near(map.TipWorld(p.Blocks[^1].End,120),new Vector3(31,42,83),"Parsed physical tip and rendered tip agree");
    }
    // Independent Rodrigues construction: C(-Z), then B(-Y) about elevated pivot.
    private static Vector3 Rotate(Vector3 p,double b,double c)
    {
        var cb=Math.Cos(b*Math.PI/180);var sb=Math.Sin(b*Math.PI/180);var cc=Math.Cos(c*Math.PI/180);var sc=Math.Sin(c*Math.PI/180);
        var x=p.X*cc+p.Y*sc;var y=-p.X*sc+p.Y*cc;var z=p.Z-50;
        return new Vector3((float)(x*cb-z*sb),(float)y,(float)(x*sb+z*cb+50));
    }
    public static void DwoAndTcp()
    {
        var p=Parse("T2 M6","G0 B35 C70","G254","G43 H2 X10 Y20 Z30","G255","G234 H2","G1 X10 Y20 Z30 F100","B60 C120");
        var map=MachineToolCoordinates.For(Machine());
        Near(map.TipWorld(p.Blocks[3].End,120),Rotate(new Vector3(30,40,80),35,70)+new Vector3(10,20,30),"DWO rotates offset only");
        Near(map.TipWorld(p.Blocks[6].End,120),Rotate(new Vector3(40,60,110),35,70),"TCPC rotates full work point");
        Check(p.Blocks[7].Path?.Samples.Count>100,"Rotary-only TCP move needs intermediate slide samples");
        foreach(var s in p.Blocks[7].Path!.Samples) Near(map.TipWorld(s,120),Rotate(new Vector3(40,60,110),s.B,s.C),"Fixed TCP through simultaneous table motion");
        var rapid=Parse("G234 H2","G1 X10 Y20 Z30 F100","G0 B45");
        Near(rapid.Blocks[2].End.X,rapid.Blocks[1].End.X,"Rapid rotary does not promise TCPC tracking");
    }
    public static void HomeAndIncremental()
    {
        var p=Parse("T2 M6","G43 H2 G0 X5 Y6 Z7","G91 G1 X2 Y-3 Z4 F100","G90 G53 G0 Z0","G0 X8","G91 G28 Z0","G90 G1 Z10");
        Near(p.Blocks[2].End.X,37,"Work incremental X");Near(p.Blocks[2].End.Y,43,"Work incremental Y");Near(p.Blocks[2].End.Z,211,"Work incremental Z");
        Near(p.Blocks[3].End.Z,0,"G53 ignores work and tool offsets");Near(p.Blocks[4].End.X,38,"G53 is nonmodal");Near(p.Blocks[4].End.Z,0,"Omitted Z remains physical zero");
        Near(p.Blocks[5].End.Z,0,"G91 G28 Z0 homes Z");Near(p.Blocks[5].End.X,38,"G28 only addressed axes");
        Near(p.Blocks[6].End.Z,210,"G28 retains G43 and resumes work coordinate mode");
        var abs=Parse("G0 X10 Y20 Z30","G28 X0","G1 X5 F100");
        Near(abs.Blocks[1].Path!.Samples[0].X,30,"G28 absolute intermediate point uses work offset");Near(abs.Blocks[1].End.X,0,"G28 finishes at machine zero");
        Check(abs.Blocks[2].Motion==MotionKind.Linear,"G28 does not overwrite modal motion");
    }
    public static void DrillingCycles()
    {
        foreach(var code in new[]{81,82,83,84,85,86}) {
            var p=Parse("T2 M6","G43 H2 G0 X0 Y0 Z10",$"G{code} G98 Z-10 R2 Q3 P200 F100","X20","G99 X30","G80","G0 X40");
            Check(p.ErrorCount==0,"Valid G"+code+" cycle");var b=p.Blocks[2];Check(b.CycleMotion!=null,"Generated cycle exists");
            Near(b.End.Z,210,"G98 returns to initial plane");Near(p.Blocks[4].End.Z,202,"G99 returns R plane");
            Near(b.CycleMotion!.Phases.Where(x=>x.Kind==CycleMotionPhaseKind.FeedIn).Min(x=>x.End.Z),190,"Cycle reaches bottom");
            Check(p.Blocks[3].CycleMotion!=null && p.Blocks[6].CycleMotion==null,"Cycle repeat and G80 cancellation");
            if(code is 84 or 85)Check(b.CycleMotion.Phases.Any(x=>x.Kind==CycleMotionPhaseKind.FeedRetract),"Tap/ream retract at feed");
            if(code==83)Check(b.CycleMotion.Phases.Count(x=>x.Kind==CycleMotionPhaseKind.FeedIn)==4,"Peck depth applied incrementally");
            if(code==82)Near(b.CycleMotion.DwellSeconds,.2,"P200 milliseconds");
        }
        var inc=Parse("G0 Z10","G91 G81 G99 X5 Z-7 R-8 F100","X5");
        Near(inc.Blocks[1].End.Z,82,"Incremental R from initial plane");Near(inc.Blocks[2].End.X,20,"Incremental repeat X");
        var unsupported=Parse("G81 Z-2 R1 F100 L3");Check(unsupported.ErrorCount==1,"Unsupported L cannot silently draw one hole");
    }
    public static void RadiusArcs()
    {
        var p=Parse("G0 X0 Y0 Z0","G1 X10 F100","G2 X0 Y10 R10");
        Check(p.Blocks[^1].Path?.Samples.Count>=8,"Fanuc R arc has samples");Check(p.WarningCount==0,"Fanuc radius accepted");
    }
    public static void CentreOnlyCirclesAndInvalidArcs()
    {
        foreach(var clockwise in new[]{false,true})
        foreach(var plane in new[]{"G17","G18","G19"}) {
            var centre=plane=="G17"?"I-5 J0":plane=="G18"?"K-5 I0":"J-5 K0";
            var p=Parse("G0 X5 Y5 Z5",plane,$"G{(clockwise?2:3)} {centre} F100",centre,"M8","F200");
            Check(p.ErrorCount==0,"Valid centre-only circles");
            foreach(var b in p.Blocks.Skip(2).Take(2)) {
                Check(b.HasMotion && b.Path!.Samples.Count>30,"Explicit and modal centre-only circles are motion");
                Near(b.Path!.Length,2*Math.PI*5,"Complete circumference",.04);
                Near(b.Start.X,b.End.X,"Circle endpoint X");Near(b.Start.Y,b.End.Y,"Circle endpoint Y");Near(b.Start.Z,b.End.Z,"Circle endpoint Z");
            }
            Check(!p.Blocks[4].HasMotion&&!p.Blocks[5].HasMotion,"Non-motion words cannot repeat modal circle");
            var player=new SimulationPlayer();player.Load(p);player.Seek(2);player.Play();player.Advance(TimeSpan.FromSeconds(1));
            Check(player.CurrentBlock!.SourceLine==3 && player.Position!=p.Blocks[2].Start,"Player traverses the circle, does not skip equal endpoints");
        }
        var bad=Parse("G0 X0 Y0 Z0","G2 X100 Y0 R1 F100","G1 X5");
        Check(bad.ErrorCount==1 && bad.Blocks[1].ExecutionBlocked && bad.Blocks[2].ExecutionBlocked,"Invalid arc must not turn into a cutting chord");
        var large=Context() with { WorkFrame=CoordinateFrame.Identity() with {Origin=new Vector3(0,190.12932f,-386.6678f)}};
        var helix=GCodeParser.Parse("float frame",new[]{"T11 M6","G0 X-35.974 Y47.731","G43 H11 Z11","G0 Z-17","G3 X-35.974 Y47.731 Z-18.581 I3.848 J2.869 F250"},Machine(true),large);
        Check(helix.Blocks[^1].Path!.Length>29 && helix.Blocks[^1].Path!.Revolutions==1,"Float coordinate recovery cannot collapse a full helix into a plunge");
    }
    public static void CompensationEntry()
    {
        foreach(var side in new[]{41,42}) {
            var p=Parse("T2 M6","G43 H2 G0 X10 Y10 Z0",$"G1 G{side} X0 Y5 D2 F100","T11 (preselection only)","M8","G91 X-5 Y-5");
            var delta=(side==41?1:-1)*5/Math.Sqrt(2);
            Near(p.Blocks[2].End.X,30+delta,"Type A entry normal follows next contour");
            Near(p.Blocks[2].End.Y,45-delta,"Type A entry Y");
        }
        var arc=Parse("T2 M6","G43 H2 G0 X20 Y5 Z0","G1 G41 X10 Y0 D2 F100","G3 X0 Y10 I-10 J0");
        Near(arc.Blocks[2].End.X,35,"Entry uses following circular tangent");Near(arc.Blocks[2].End.Y,40,"Circular entry Y");
        var shortArc=Parse("G0 X10 Y0 Z0","G3 X10 Y0.001 I-10 J0 F100");
        Check(shortArc.Blocks[^1].Path!.Length<.01,"Resolvable short arc is not promoted to a full circle");
    }
    public static void CancelledSetupDiagnostics()
    {
        var p=Parse("T2 M6","G254","G43 H2 G0 X5 Y5 Z10","G49 G255","G0 B45","G1 X2 F100","G1 X3");
        Check(p.ErrorCount==0,"Missing restoration can be intentional: do not invent control commands");
        Check(p.Blocks[5].Warning?.Contains("G49")==true && p.Blocks[5].Warning?.Contains("G255")==true,"Cancelled setup is explained at first feed");
        Check(p.Blocks[6].Warning is null,"Do not flood subsequent blocks");
        var different=Parse("T2 M6","G43 H11 G1 Z0 F100");
        Check(different.ErrorCount==0 && different.Blocks[^1].Warning?.Contains("H11")==true,"Different H is legal but unequal known lengths are visible");
    }
    public static void OutwardToolFaces()
    {
        var tool=new JobTool("T2","profile","Mill",12,0,60,"holder",null,"Mill5",20,12,0,0,0,"","nx-parametric-tool-builder",Array.Empty<ToolProfileSection>(),new[]{new ToolProfileSection(30,20,15,0)});
        foreach(var part in ParametricToolMeshBuilder.BuildParts(tool)) {
            var m=part.Mesh;double volume=0;
            Vector3 P(int index)=>new(m.Positions[index*3],m.Positions[index*3+1],m.Positions[index*3+2]);
            for(int i=0;i<m.Indices.Length;i+=3) {
                var a=P(m.Indices[i]);var b=P(m.Indices[i+1]);var c=P(m.Indices[i+2]);var n=Vector3.Cross(b-a,c-a);
                volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6.0;
                var center=(a+b+c)/3;Check(n.Y*center.Y+n.Z*center.Z>=-.001,"Side faces point outward for GPU culling");
            }
            Check(volume>0,"Closed tool region has outward signed volume: "+part.Role);
        }
    }
}
