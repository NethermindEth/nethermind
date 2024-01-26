using System.Numerics;

namespace Nethermind.Crypto.PairingCurves;

public interface ICurveParams<T> where T : IBaseField
{
    (Fq<T>, Fq<T>) G1FromX(Fq<T> x, bool sign);
    bool G1IsOnCurve((Fq<T>, Fq<T>)? p);
    (Fq2<T>, Fq2<T>) G2FromX(Fq2<T> x, bool sign);
    bool G2IsOnCurve((Fq2<T>, Fq2<T>)? p);
    BigInteger GetSubgroupOrder();
    BigInteger GetX();
    T GetBaseField();
}
