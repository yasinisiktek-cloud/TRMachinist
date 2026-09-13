using System.Collections.Concurrent;
using System.Numerics;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    private TripleDexelVolume SubtractProfileRows(Vector3 minimum,Vector3 maximum,Vector3 travel,bool swept,
        PreparedCylinderRay[] xKernels,PreparedCylinderRay[] yKernels,PreparedCylinderRay[] zKernels,int tag)
    {
        var before=Volume();
        var changed=new ConcurrentBag<Bounds3>();
        var xU=CoordinateRange(_xField.U,minimum.Y,maximum.Y);var xV=CoordinateRange(_xField.V,minimum.Z,maximum.Z);
        var yU=CoordinateRange(_yField.U,minimum.X,maximum.X);var yV=CoordinateRange(_yField.V,minimum.Z,maximum.Z);
        var zU=CoordinateRange(_zField.U,minimum.X,maximum.X);var zV=CoordinateRange(_zField.V,minimum.Y,maximum.Y);
        RunIndependentFieldPasses(
            RangeArea(xU.Item1,xU.Item2,xV.Item1,xV.Item2)+RangeArea(yU.Item1,yU.Item2,yV.Item1,yV.Item2)+RangeArea(zU.Item1,zU.Item2,zV.Item1,zV.Item2),
            ()=>Pass(_xField,Axis3.X,xKernels,xU,xV,minimum.X,maximum.X),
            ()=>Pass(_yField,Axis3.Y,yKernels,yU,yV,minimum.Y,maximum.Y),
            ()=>Pass(_zField,Axis3.Z,zKernels,zU,zV,minimum.Z,maximum.Z));
        foreach(var bounds in changed.OrderBy(b=>b.Min.Z).ThenBy(b=>b.Min.Y).ThenBy(b=>b.Min.X)
            .ThenBy(b=>b.Max.Z).ThenBy(b=>b.Max.Y).ThenBy(b=>b.Max.X)) MarkSurfaceChunksDirty(bounds);
        var after=Volume();
        return new(Math.Max(0,before.X-after.X),Math.Max(0,before.Y-after.Y),Math.Max(0,before.Z-after.Z));

        void Pass(DexelField field,Axis3 axis,PreparedCylinderRay[] kernels,(int,int) us,(int,int) vs,double low,double high)
        {
            var bounds=swept ? null : kernels.Select(kernel=>PrepareStaticProfileRayBounds(kernel)).ToArray();
            var prepared=swept ? kernels.Select(kernel=>PrepareFrustumSweep(kernel,travel)).ToArray() : [];
            // Rays within different rows are independent. Small stocks retain
            // the sequential fallback used by the other stock kernels.
            ForIndependentRows(vs.Item1,vs.Item2,RangeArea(us.Item1,us.Item2,vs.Item1,vs.Item2),v=>
            {
                var intervals=new CylinderRayInterval[kernels.Length];
                var rowMin=new Vector3(float.PositiveInfinity);var rowMax=new Vector3(float.NegativeInfinity);
                for(int u=us.Item1;u<=us.Item2;u++)
                {
                    if(!field.Ray(u,v).MayOverlap(low,high))continue;
                    var origin=Point(0,u,v);
                    int count=swept?CollectPreparedSweptIntervals(origin,prepared,intervals):CollectCylinderIntervals(origin,kernels,intervals,bounds);
                    for(int i=0;i<count;i++)
                        if(field.SubtractThreadSafe(u,v,intervals[i].Minimum,intervals[i].Maximum,tag,out var lo,out var hi)>0)
                            IncludeChangedBounds(ref rowMin,ref rowMax,Point((float)lo,u,v),Point((float)hi,u,v));
                }
                if(_surfaceCacheEnabled && float.IsFinite(rowMin.X))changed.Add(new(rowMin,rowMax));
            });
            Vector3 Point(float longitudinal,int u,int v) => axis switch {
                Axis3.X=>new(longitudinal,(float)field.U[u],(float)field.V[v]),
                Axis3.Y=>new((float)field.U[u],longitudinal,(float)field.V[v]),
                _=>new((float)field.U[u],(float)field.V[v],longitudinal) };
        }
    }
}
