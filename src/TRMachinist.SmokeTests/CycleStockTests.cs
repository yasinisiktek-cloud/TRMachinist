using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using TRMachinist.Core;

internal static class CycleStockTests
{
    public static void ProgressivePecks()
    {
        var top=new AxisState { Z=9 }; var r=top with { Z=4 }; var bottom=r with { Z=-4 };
        var cycle=new GeneratedCycleMotion("G83",1,1,new[] {
            new CycleMotionPhase(CycleMotionPhaseKind.RapidApproach,top,r,6000),
            new CycleMotionPhase(CycleMotionPhaseKind.FeedIn,r,bottom,120),
            new CycleMotionPhase(CycleMotionPhaseKind.Dwell,bottom,bottom,0,.2),
            new CycleMotionPhase(CycleMotionPhaseKind.PeckRetract,bottom,top,6000)},.2);
        if(CycleStockProgress.Select(cycle,0,.001).Count!=0)throw new Exception("Rapid approach must never cut.");
        if(CycleStockProgress.Select(cycle,0,.5).Count!=1)throw new Exception("Cutting was deferred until the hole ended.");
        if(CycleStockProgress.Select(cycle,.999,1).Count!=0)throw new Exception("Rapid retract must never cut.");
        foreach(var axis in new[]{Vector3.UnitZ,Vector3.Normalize(new Vector3(.4f,-.6f,1))})
        foreach(var frames in new[]{1,7,31,123})
        {
            var bounds=new Bounds3(new(-7,-7,-7),new(7,7,7));
            var reference=new TripleDexelStock(bounds,.15);var actual=new TripleDexelStock(bounds,.15);
            var layers=new[]{new CutterCylinderLayer(0,.5,9),new CutterCylinderLayer(.6,1.3,8.4)};
            var a=axis*5;var b=-axis*4;
            reference.ApplyLayeredCutterMove(a,b,axis,axis,layers,false,5);
            for(int f=0;f<frames;f++)actual.ApplyLayeredCutterMoveProgress(a,b,axis,axis,layers,f/(double)frames,(f+1)/(double)frames,5);
            if(reference.Volume()!=actual.Volume() || Hash(reference)!=Hash(actual))
                throw new Exception($"Cycle stock depends on playback slices: {axis}/{frames}.");
        }
        static string Hash(TripleDexelStock stock)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach(var c in stock.BuildAllSurfaceChunks().OrderBy(c=>c.Key.Z).ThenBy(c=>c.Key.Y).ThenBy(c=>c.Key.X))
            foreach(var m in c.Meshes) {
                hash.AppendData(BitConverter.GetBytes(m.Tag));
                hash.AppendData(MemoryMarshal.AsBytes(m.Mesh.Positions.AsSpan()));
                hash.AppendData(MemoryMarshal.AsBytes(m.Mesh.Normals.AsSpan()));
                hash.AppendData(MemoryMarshal.AsBytes(m.Mesh.Indices.AsSpan()));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
