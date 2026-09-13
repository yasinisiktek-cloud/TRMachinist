using System.Numerics;
using System.Text;

namespace TRMachinist.Core;

internal sealed class FanucModalState
{
    public bool DynamicOffset { get; private set; }
    public bool Tcp { get; private set; }
    public bool LengthCompensation { get; private set; }
    public int? LengthRegister { get; private set; }
    public string? Error { get; private set; }
    public bool Changed { get; private set; }
    public FanucModalCycles Cycles { get; } = new();
    private bool lengthWasCancelled, dynamicWasCancelled;
    private readonly HashSet<string> reported = new();
    public static bool Applies(MachinePackage machine)=>machine.ControllerFamily.Contains("Fanuc",StringComparison.OrdinalIgnoreCase)
                                                        || machine.ControllerFamily.Contains("Haas",StringComparison.OrdinalIgnoreCase);

    public void Update(MachinePackage machine,IReadOnlyList<(char Letter,double Value)> words,bool toolChange)
    {
        var before=(DynamicOffset,Tcp,LengthCompensation,LengthRegister);
        Error=null;
        bool Has(char letter,double value)=>words.Any(w=>w.Letter==letter && Math.Abs(w.Value-value)<1e-6);
        bool haas=machine.ControllerFamily.Contains("Haas",StringComparison.OrdinalIgnoreCase)
                  || (machine.ControllerCommands.Contains("G254") && machine.ControllerCommands.Contains("G234"));
        if (toolChange) { Tcp=false;LengthCompensation=false;LengthRegister=null; }
        if (Has('G',255)) { dynamicWasCancelled |= DynamicOffset; DynamicOffset=false; }
        if (Has('G',49)) { lengthWasCancelled |= LengthCompensation; Tcp=false;LengthCompensation=false;LengthRegister=null; }
        if (Has('G',43)) { Tcp=false;LengthCompensation=true; }
        var h=words.Where(w=>w.Letter=='H').Select(w=>(int)Math.Round(w.Value)).LastOrDefault(-1);
        if (h>=0) LengthRegister=h;
        if (Has('G',254)) {
            if (!haas) Error="G254 için Haas DWO kumanda tanımı yok.";
            else if(Tcp)Error="G254 DWO ile G234 TCPC birlikte etkin olamaz.";
            else DynamicOffset=true;
        }
        if (Has('G',234)) {
            if (!haas)Error="G234 için Haas TCPC kumanda tanımı yok.";
            else if(DynamicOffset)Error="G234 TCPC öncesinde G255 ile DWO kapatılmalıdır.";
            else if(h<0)Error="G234 aynı blokta bir H takım boyu kaydı gerektirir.";
            else {Tcp=true;LengthCompensation=true;}
        }
        if(Has('M',2)||Has('M',30)){Tcp=false;DynamicOffset=false;LengthCompensation=false;LengthRegister=null;}
        // Fractional codes are distinct commands; G43.4 must never be rounded to G43.
        foreach(var g in words.Where(w=>w.Letter=='G'))
            if(Math.Abs(g.Value-Math.Round(g.Value))>1e-6)
                Error=FormattableString.Invariant($"G{g.Value:0.###} bu kumanda profilinde henüz desteklenmiyor.");
            else if (g.Value is not (0 or 1 or 2 or 3 or 17 or 18 or 19 or 20 or 21 or 28 or
                     40 or 41 or 42 or 43 or 49 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or
                     80 or 81 or 82 or 83 or 84 or 85 or 86 or 90 or 91 or 94 or 98 or 99 or 234 or 254 or 255))
                Error=FormattableString.Invariant($"G{g.Value:0.###} bu kumanda profilinde henüz desteklenmiyor; hareket doğrulanamadı.");
        Changed=before!=(DynamicOffset,Tcp,LengthCompensation,LengthRegister);
    }
    // These are NC setup diagnostics, not permission to invent missing G/H
    // commands. H and T may legitimately differ, so a mismatch is advisory.
    public string? CheckCuttingState(GCodeSimulationContext context,int? tool,AxisState pose)
    {
        var messages=new List<string>();
        if(lengthWasCancelled && !LengthCompensation && tool.HasValue && reported.Add("cancelled-length"))
            messages.Add("G49 sonrası takım boyu telafisi yeniden açılmadan ilerleme hareketi var. NC'de G43/G234 ve H kaydını kontrol edin; takım ucu boy kadar sapabilir.");
        if(dynamicWasCancelled && !DynamicOffset && !Tcp && (Math.Abs(pose.B)>.001 || Math.Abs(pose.C)>.001) && reported.Add("cancelled-dwo"))
            messages.Add("G255 sonrası tabla dönükken DWO/TCPC kapalı ilerleme var. XYZ sabit G54 çerçevesinde uygulanır; NC'deki G254/iş sıfırı seçimini kontrol edin.");
        if(LengthCompensation && LengthRegister is { } h && tool is { } t && h!=t &&
           context.ToolGaugeLengths.TryGetValue(h,out var registerGauge) && context.ToolGaugeLengths.TryGetValue(t,out var toolGauge) &&
           Math.Abs(registerGauge-toolGauge)>.01 && reported.Add($"length:{t}:{h}"))
            messages.Add(FormattableString.Invariant($"T{t} için H{h} kullanılıyor: H boyu {registerGauge:0.###} mm, takılı takım boyu {toolGauge:0.###} mm. Fark {registerGauge-toolGauge:0.###} mm; H=T varsayılmadı. İş paketinde ayrı H tablosu bulunmuyor, eşleşmeyi kontrol edin."));
        return messages.Count==0?null:string.Join(" ",messages);
    }
    public double Gauge(GCodeSimulationContext context,out string? error)
    {
        error=null;
        if(!LengthCompensation || LengthRegister==0)return 0;
        if(LengthRegister is { } h && context.ToolGaugeLengths.TryGetValue(h,out var gauge))return gauge;
        error=$"G43/G234 takım boyu H{LengthRegister?.ToString() ?? "?"} iş paketinde çözülemedi.";
        return 0;
    }
    public CoordinateFrame WorkFrame(MachinePackage machine,CoordinateFrame original,AxisState pose)
        =>DynamicOffset ? original with { Origin=MachineCoordinateResolver.TransformWorkpiecePoint(machine,original.Origin,pose.B,pose.C),Source="haas:G254-dynamic-origin" } : original;

    public static string WithoutComments(string raw)
    {
        var result=new StringBuilder(raw.Length);int depth=0;bool quoted=false;
        foreach(var c in raw) {
            if(c=='"'&&depth==0)quoted=!quoted;
            if(!quoted && c=='('){depth++;continue;}
            if(!quoted && c==')' && depth>0){depth--;continue;}
            if(depth==0)result.Append(c);
        }
        return result.ToString();
    }
}
