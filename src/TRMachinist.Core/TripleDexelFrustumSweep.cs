using System.Numerics;

namespace TRMachinist.Core;

public sealed partial class TripleDexelStock
{
    private readonly record struct D3(double X, double Y, double Z)
    {
        public static D3 operator +(D3 a, D3 b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static D3 operator -(D3 a, D3 b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static D3 operator *(D3 a, double b) => new(a.X*b,a.Y*b,a.Z*b);
        public double Dot(D3 b) => X*b.X+Y*b.Y+Z*b.Z;
    }

    private readonly record struct PreparedFrustumSweep(
        Vector3 Tip, D3 Axis, D3 Direction, D3 RayRadial, D3 TravelRadial,
        double AxialRay, double AxialTravel, double StartRadius, double RadiusSlope,
        double RayRadius, double TravelRadius, double Length, double TranslationQuadratic,
        bool HasStationaryCandidate, double StationarySlope);

    private static PreparedFrustumSweep PrepareFrustumSweep(in PreparedCylinderRay kernel, Vector3 travel)
    {
        var axis = new D3(kernel.Axis.X,kernel.Axis.Y,kernel.Axis.Z);
        axis *= 1 / Math.Sqrt(axis.Dot(axis));
        var dir = new D3(kernel.Direction.X,kernel.Direction.Y,kernel.Direction.Z);
        var move = new D3(-travel.X,-travel.Y,-travel.Z);
        double st=dir.Dot(axis), su=move.Dot(axis);
        var rt=dir-axis*st; var ru=move-axis*su;
        double radiust=kernel.RadiusSlope*st, radiusu=kernel.RadiusSlope*su;
        double c=ru.Dot(ru)-radiusu*radiusu;
        bool stationary=c>1e-12*Math.Max(1,ru.Dot(ru)+radiusu*radiusu);
        return new(kernel.Tip,axis,dir,rt,ru,st,su,kernel.StartRadius,kernel.RadiusSlope,
            radiust,radiusu,kernel.Length,c,stationary,
            stationary ? -(ru.Dot(rt)-radiusu*radiust)/c : 0);
    }

    // Exact translation of a finite convex frustum along one straight segment.
    // For a ray coordinate t, cutter translation u is restricted to [0,1] and
    // the axial caps. Its radial inequality is quadratic in u. The minimum can
    // occur only at either segment end, either cap, or its stationary point.
    // Testing those five affine u(t) candidates avoids repeatedly stamping a
    // large inclined cutter, while retaining the full continuous swept volume.
    private static bool SweptFrustumLineInterval(Vector3 origin, in PreparedCylinderRay kernel,
        Vector3 travel, out double minimum, out double maximum)
    {
        var prepared=PrepareFrustumSweep(kernel,travel);
        return PreparedSweptFrustumLineInterval(origin,prepared,out minimum,out maximum);
    }

    // These coefficients belong to the move and section, not to an individual
    // stock ray. The projection and stable root arithmetic below are unchanged.
    private static bool PreparedSweptFrustumLineInterval(Vector3 origin, in PreparedFrustumSweep kernel,
        out double minimum, out double maximum)
    {
        var axis=kernel.Axis; var dir=kernel.Direction;
        var offset = new D3((double)origin.X-kernel.Tip.X,(double)origin.Y-kernel.Tip.Y,(double)origin.Z-kernel.Tip.Z);
        var anchor = -offset.Dot(dir); offset += dir*anchor;
        double s0=offset.Dot(axis), st=kernel.AxialRay, su=kernel.AxialTravel;
        var r0=offset-axis*s0; var rt=kernel.RayRadial; var ru=kernel.TravelRadial;
        double radius0=kernel.StartRadius+kernel.RadiusSlope*s0;
        double radiust=kernel.RayRadius, radiusu=kernel.TravelRadius;
        double length=kernel.Length;
        double lo=double.PositiveInfinity,hi=double.NegativeInfinity;
        Candidate(0,0); Candidate(0,1);
        if(Math.Abs(su)>1e-12) { Candidate(-st/su,-s0/su); Candidate(-st/su,(length-s0)/su); }
        if(kernel.HasStationaryCandidate)
            Candidate(kernel.StationarySlope, -(ru.Dot(r0)-radiusu*radius0)/kernel.TranslationQuadratic);
        minimum=lo+anchor; maximum=hi+anchor;
        return hi>lo+1e-10;

        void Candidate(double ut,double u0)
        {
            double first=double.NegativeInfinity,last=double.PositiveInfinity;
            if(!Slab(u0,ut,0,1,ref first,ref last) ||
               !Slab(s0+su*u0,st+su*ut,0,length,ref first,ref last)) return;
            // Nearly lateral motion makes a cap candidate's u(t) very steep.
            // Evaluate around its finite admissible interval, where u is in
            // [0,1], instead of squaring huge terms that later cancel.
            double center=double.IsFinite(first) && double.IsFinite(last) ? first+(last-first)*.5 : 0;
            double atCenter=u0+ut*center;
            first-=center;last-=center;
            var p=r0+rt*center+ru*atCenter; var d=rt+ru*ut;
            double pr=radius0+radiust*center+radiusu*atCenter,dr=radiust+radiusu*ut;
            double a=d.Dot(d)-dr*dr,b=2*(p.Dot(d)-pr*dr),cc=p.Dot(p)-pr*pr;
            if(Math.Abs(a)<=1e-12*Math.Max(1,d.Dot(d)+dr*dr))
            {
                if(Math.Abs(b)<=1e-12) { if(cc<=1e-9) Emit(first,last); return; }
                double root=-cc/b;
                if(b>0) Emit(first,Math.Min(last,root)); else Emit(Math.Max(first,root),last);
                return;
            }
            double disc=b*b-4*a*cc,tolerance=1e-12*Math.Max(1,b*b+Math.Abs(4*a*cc));
            if(disc < -tolerance) { if(a<0) Emit(first,last); return; }
            double q=-.5*(b+Math.CopySign(Math.Sqrt(Math.Max(0,disc)),b));
            double one=q/a,two=q==0?-b/(2*a):cc/q;
            if(one>two)(one,two)=(two,one);
            if(a>0) Emit(Math.Max(first,one),Math.Min(last,two));
            else { Emit(first,Math.Min(last,one));Emit(Math.Max(first,two),last); }
            void Emit(double a,double b) => Add(a+center,b+center);
        }
        void Add(double a,double b) { if(b>a+1e-10) {lo=Math.Min(lo,a);hi=Math.Max(hi,b);} }
    }

    private static bool Slab(double origin,double direction,double min,double max,ref double lo,ref double hi)
    {
        if(Math.Abs(direction)<=1e-12) return origin>=min-1e-10 && origin<=max+1e-10;
        double a=(min-origin)/direction,b=(max-origin)/direction;
        if(a>b)(a,b)=(b,a);
        lo=Math.Max(lo,a);hi=Math.Min(hi,b);
        return hi>lo+1e-10;
    }

    private static int CollectPreparedSweptIntervals(Vector3 origin, PreparedFrustumSweep[] kernels,
        CylinderRayInterval[] intervals)
    {
        int count=0;
        for(int section=0;section<kernels.Length;section++)
        {
            if(PreparedSweptFrustumLineInterval(origin,kernels[section],out var lo,out var hi))
                intervals[count++]=new(lo,hi);
        }
        if(count<2)return count;
        // Profiles normally contain only a few sections. Sort in place without
        // allocating a comparer for every stock ray.
        for(int i=1;i<count;i++)
        {
            var value=intervals[i];int j=i-1;
            while(j>=0 && intervals[j].Minimum>value.Minimum){intervals[j+1]=intervals[j];j--;}
            intervals[j+1]=value;
        }
        int output=0;
        for(int i=1;i<count;i++)
            if(intervals[i].Minimum<=intervals[output].Maximum+1e-9)
                intervals[output]=new(intervals[output].Minimum,Math.Max(intervals[output].Maximum,intervals[i].Maximum));
            else intervals[++output]=intervals[i];
        return output+1;
    }
}
