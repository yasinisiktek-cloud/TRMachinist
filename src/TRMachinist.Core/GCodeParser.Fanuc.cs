using System.Numerics;

namespace TRMachinist.Core;

public static partial class GCodeParser
{
    private static bool TryFanucEntryTangent(IReadOnlyList<string> lines,int nextLine,Vector3 entry,
        bool absolute,double scale,char plane,out Vector3 tangent)
    {
        tangent=Vector3.Zero;
        var mode=MotionKind.Linear;
        var normal=plane=='X'?Vector3.UnitX:plane=='Y'?Vector3.UnitY:Vector3.UnitZ;
        for(var i=nextLine;i<Math.Min(lines.Count,nextLine+64);i++)
        {
            var text=StripQuotedStrings(Clean(FanucModalState.WithoutComments(lines[i]))).ToUpperInvariant();
            var words=WordRegex().Matches(text).Select(m=>(Letter:m.Groups["letter"].Value[0],Value:ParseNumber(m.Groups["value"].Value))).ToArray();
            if(words.Any(w=>w.Letter=='M'&&w.Value is 2 or 6 or 30 || w.Letter=='G'&&w.Value is 0 or 17 or 18 or 19 or 28 or 40 or 41 or 42 or 43 or 49 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 234 or 254 or 255))return false;
            foreach(var w in words.Where(w=>w.Letter=='G'))
                switch(w.Value) {case 1:mode=MotionKind.Linear;break;case 2:mode=MotionKind.CircularClockwise;break;case 3:mode=MotionKind.CircularCounterClockwise;break;case 90:absolute=true;break;case 91:absolute=false;break;case 20:scale=25.4;break;case 21:scale=1;break;}
            float Value(char c)=> (float)(words.LastOrDefault(w=>w.Letter==c).Value*scale);
            if(mode is MotionKind.CircularClockwise or MotionKind.CircularCounterClockwise && words.Any(w=>w.Letter is 'I' or 'J' or 'K'))
            {
                var radius=-new Vector3(Value('I'),Value('J'),Value('K'));
                tangent=Vector3.Cross(normal,radius)*(mode==MotionKind.CircularClockwise?-1:1);
            }
            else
            {
                var end=entry;
                foreach(var w in words.Where(w=>w.Letter is 'X' or 'Y' or 'Z')) {
                    var value=(float)(w.Value*scale);
                    if(w.Letter=='X')end.X=absolute?value:entry.X+value;
                    if(w.Letter=='Y')end.Y=absolute?value:entry.Y+value;
                    if(w.Letter=='Z')end.Z=absolute?value:entry.Z+value;
                }
                tangent=end-entry;
            }
            tangent-=normal*Vector3.Dot(normal,tangent);
            if(tangent.LengthSquared()>1e-10f)return true;
        }
        return false;
    }
    private static double LinearDistance(AxisState a, AxisState b) =>
        Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2)+Math.Pow(a.Z-b.Z,2));

    private static MotionPath BuildTcpLinearPath(MachinePackage machine, CoordinateFrame frame,
        Vector3 startWork, Vector3 endWork, AxisState start, AxisState end, double gauge)
    {
        // Resolve the programmed straight line while the table turns. Linear
        // interpolation between just the two slide endpoints would bow the TCP.
        var angle = Math.Abs(end.B-start.B)+Math.Abs(end.C-start.C);
        var count = Math.Clamp((int)Math.Ceiling(angle/.25),1,100000);
        var samples = new List<AxisState>(count);
        var coordinates = MachineToolCoordinates.For(machine);
        var previous = start; double length = 0;
        for (int i=1; i<=count; i++)
        {
            var t=(double)i/count;
            var work=Vector3.Lerp(startWork,endWork,(float)t);
            var pose=new AxisState(0,0,0,start.A+(end.A-start.A)*t,start.B+(end.B-start.B)*t,start.C+(end.C-start.C)*t);
            var point=frame.Origin+frame.XAxis*work.X+frame.YAxis*work.Y+frame.ZAxis*work.Z;
            point=MachineCoordinateResolver.TransformWorkpiecePoint(machine,point,pose.B,pose.C);
            var sample=i==count?end:coordinates.SlidesForTip(point,pose,gauge);
            length+=LinearDistance(previous,sample);samples.Add(sample);previous=sample;
        }
        return new MotionPath(samples,length,0);
    }
}
