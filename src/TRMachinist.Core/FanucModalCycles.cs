using System.Numerics;

namespace TRMachinist.Core;

internal sealed class FanucModalCycles
{
    public int Code { get; private set; }
    private bool initialReturn=true;
    private int activationLine;
    private double? depth,retract,initial;
    private double peck,dwell;
    private bool newCycle;
    private bool suppress;
    public bool Active=>Code!=0;
    public double ReturnLevel=>initialReturn?Math.Max(initial!.Value,retract!.Value):retract!.Value;
    public void Update(IReadOnlyList<(char Letter,double Value)> words,int line,double scale,string raw)
    {
        bool Has(double g)=>words.Any(w=>w.Letter=='G'&&Math.Abs(w.Value-g)<1e-6);
        if(Has(98))initialReturn=true;if(Has(99))initialReturn=false;
        if(Has(80)||words.Any(w=>w.Letter=='G'&&w.Value is 0 or 1 or 2 or 3)) {Code=0;initial=null;depth=null;retract=null;peck=0;dwell=0;}
        var declaration=words.FirstOrDefault(w=>w.Letter=='G'&&w.Value>=81&&w.Value<=86&&w.Value==Math.Floor(w.Value));
        newCycle=declaration.Letter=='G' && Code==0;
        if(declaration.Letter=='G') {Code=(int)declaration.Value;activationLine=line;}
        suppress=words.Any(w=>w.Letter=='L'&&w.Value==0);
        if(!Active)return;
        if(words.Any(w=>w.Letter=='Q'))peck=words.Last(w=>w.Letter=='Q').Value*scale;
        if(words.Any(w=>w.Letter=='P')) {
            var p=words.Last(w=>w.Letter=='P').Value;
            // Haas: integer P is milliseconds; a decimal-point P is seconds.
            var token=System.Text.RegularExpressions.Regex.Match(raw,@"\bP\s*([+-]?[\d.]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            dwell=token.Success&&token.Groups[1].Value.Contains('.')?p:p/1000.0;
        }
    }
    public bool Triggers(IReadOnlyList<(char Letter,double Value)> words)=>Active&&!suppress&&words.Any(w=>w.Letter is 'X' or 'Y' or 'Z' or 'R');

    public (AxisState End,MotionPath Path,GeneratedCycleMotion Motion) Generate(
        IReadOnlyList<(char Letter,double Value)> words,bool absolute,double scale,int line,
        Vector3 startWork,Vector3 positionedWork,AxisState start,double feed,Func<Vector3,AxisState> map)
    {
        if(newCycle||!initial.HasValue)initial=startWork.Z;
        if(words.Any(w=>w.Letter=='L' && w.Value!=1))
            throw new InvalidDataException("L tekrarlı Fanuc çevrimi bu sürümde desteklenmiyor.");
        if(words.Any(w=>w.Letter is 'I' or 'J' or 'K'))
            throw new InvalidDataException("Değişken I/J/K pasolu delme çevrimi bu sürümde desteklenmiyor.");
        if(words.Any(w=>w.Letter=='R')) {
            var value=words.Last(w=>w.Letter=='R').Value*scale;
            retract=absolute?value:initial.Value+value;
        }
        if(words.Any(w=>w.Letter=='Z')) {
            var value=words.Last(w=>w.Letter=='Z').Value*scale;
            depth=absolute?value:(retract??initial.Value)+value;
        }
        if(!depth.HasValue || !retract.HasValue || feed<=0)
            throw new InvalidDataException($"G{Code}: Z derinliği, R düzlemi ve pozitif ilerleme gereklidir.");
        if(Code==83 && peck<=0)throw new InvalidDataException("G83 için pozitif Q paso derinliği gerekir.");
        if(dwell<0 || !double.IsFinite(dwell))throw new InvalidDataException("Çevrim bekleme süresi geçersiz.");
        var phases=new List<CycleMotionPhase>();var samples=new List<AxisState>();var current=start;double length=0;
        void Add(CycleMotionPhaseKind kind,Vector3 point,double seconds=0) {
            var target=map(point);phases.Add(new CycleMotionPhase(kind,current,target,feed,seconds));samples.Add(target);
            length+=Math.Sqrt(Math.Pow(target.X-current.X,2)+Math.Pow(target.Y-current.Y,2)+Math.Pow(target.Z-current.Z,2));current=target;
        }
        Vector3 At(double z)=>new(positionedWork.X,positionedWork.Y,(float)z);
        // Keep clearance while positioning to the next hole.
        var high=Math.Max(startWork.Z,retract.Value);
        if(startWork.Z<retract.Value)Add(CycleMotionPhaseKind.RapidRetract,new Vector3(startWork.X,startWork.Y,(float)high));
        Add(CycleMotionPhaseKind.RapidPosition,At(high));
        Add(CycleMotionPhaseKind.RapidApproach,At(retract.Value));
        if(Code==83) {
            var span=Math.Abs(depth.Value-retract.Value);var count=(int)Math.Ceiling(span/peck);
            if(count>100000)throw new InvalidDataException("G83 paso sayısı sınırı aşıldı.");
            var direction=Math.Sign(depth.Value-retract.Value);
            for(int i=1;i<=count;i++) {
                var next=retract.Value+direction*Math.Min(span,i*peck);
                // Conservatively feed from R after each full retract: no unknown
                // machine-setting clearance is invented for the rapid re-entry.
                Add(CycleMotionPhaseKind.FeedIn,At(next));
                if(i<count)Add(CycleMotionPhaseKind.PeckRetract,At(retract.Value));
            }
        } else Add(CycleMotionPhaseKind.FeedIn,At(depth.Value));
        if(dwell>0 && Code is 82 or 83 or 86)Add(CycleMotionPhaseKind.Dwell,At(depth.Value),dwell);
        Add(Code is 84 or 85?CycleMotionPhaseKind.FeedRetract:CycleMotionPhaseKind.RapidRetract,At(retract.Value));
        if(initialReturn && initial.Value>retract.Value)Add(CycleMotionPhaseKind.RapidReturnToProgrammedLevel,At(initial.Value));
        newCycle=false;
        return(current,new MotionPath(samples,length,0),new GeneratedCycleMotion("G"+Code,line,activationLine,phases,phases.Sum(p=>p.DwellSeconds)));
    }
}
