using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Polynomial;

public class LagrangeBasis(FrE[] evaluations)
{
    // private readonly int _domain = evaluations.Length;
    public readonly FrE[] Evaluations = evaluations;

    private static LagrangeBasis Add(LagrangeBasis lhs, LagrangeBasis rhs)
    {
        FrE[] result = new FrE[lhs.Evaluations.Length];

        Parallel.For(0, lhs.Evaluations.Length, i =>
        {
            result[i] = lhs.Evaluations[i] + rhs.Evaluations[i];
        });

        return new LagrangeBasis(result);
    }

    private static LagrangeBasis Sub(LagrangeBasis lhs, LagrangeBasis rhs)
    {
        FrE[] result = new FrE[lhs.Evaluations.Length];

        Parallel.For(0, lhs.Evaluations.Length, i =>
        {
            result[i] = lhs.Evaluations[i] - rhs.Evaluations[i];
        });

        return new LagrangeBasis(result);
    }

    private static LagrangeBasis Mul(LagrangeBasis lhs, LagrangeBasis rhs)
    {
        FrE[] result = new FrE[lhs.Evaluations.Length];

        Parallel.For(0, lhs.Evaluations.Length, i =>
        {
            result[i] = lhs.Evaluations[i] * rhs.Evaluations[i];
        });

        return new LagrangeBasis(result);
    }

    private static LagrangeBasis Scale(LagrangeBasis poly, FrE constant)
    {
        FrE[] result = new FrE[poly.Evaluations.Length];

        for (int i = 0; i < poly.Evaluations.Length; i++) result[i] = poly.Evaluations[i] * constant;

        return new LagrangeBasis(result);
    }

    // private FrE[] GenerateDomainPoly()
    // {
    //     FrE[] domain = new FrE[_domain];
    //     for (int i = 0; i < _domain; i++) domain[i] = FrE.SetElement(i);
    //
    //     return domain;
    // }

    // public FrE EvaluateOutsideDomain(LagrangeBasis precomputedWeights, FrE z)
    // {
    //     FrE[] domain = GenerateDomainPoly();
    //     MonomialBasis a = MonomialBasis.VanishingPoly(domain);
    //     FrE az = a.Evaluate(z);
    //
    //     if (az.IsZero) ThrowVanishingPolynomialEvaluatedToZeroException();
    //
    //
    //     FrE[] inverses = FrE.MultiInverse(domain.Select(x => z - x).ToArray());
    //     IEnumerable<FrE> helperVector = precomputedWeights.Evaluations.Zip(Evaluations)
    //         .Select((elements, _) => elements.First * elements.Second);
    //     FrE r = helperVector.Zip(inverses).Select((elem, i) => elem.First * elem.Second)
    //         .Aggregate(FrE.Zero, (current, elem) => current + elem);
    //
    //     r *= az;
    //
    //     return r;
    // }

    // public MonomialBasis Interpolate()
    // {
    //     FrE[] xs = GenerateDomainPoly();
    //     FrE[] ys = Evaluations;
    //
    //     MonomialBasis root = MonomialBasis.VanishingPoly(xs);
    //     if (root.Length() != ys.Length + 1) ThrowVanishingPolyLengthException();
    //
    //     List<MonomialBasis> nums = xs.Select(x => new[] { x.Negative(), FrE.One })
    //         .Select(s => root / new MonomialBasis(s))
    //         .ToList();
    //
    //     FrE[] invDenominators = FrE.MultiInverse(xs.Select((t, i) => nums[i].Evaluate(t)).ToArray());
    //
    //     FrE[] b = new FrE[ys.Length];
    //
    //     for (int i = 0; i < xs.Length; i++)
    //     {
    //         FrE ySlice = ys[i] * invDenominators[i];
    //         for (int j = 0; j < ys.Length; j++) b[j] += nums[i].Coeffs[j] * ySlice;
    //     }
    //
    //     while (b.Length > 0 && b[^1].IsZero) Array.Resize(ref b, b.Length - 1);
    //
    //     return new MonomialBasis(b);
    // }

    public static LagrangeBasis operator +(in LagrangeBasis a, in LagrangeBasis b)
    {
        return Add(a, b);
    }

    public static LagrangeBasis operator -(in LagrangeBasis a, in LagrangeBasis b)
    {
        return Sub(a, b);
    }

    public static LagrangeBasis operator *(in LagrangeBasis a, in LagrangeBasis b)
    {
        return Mul(a, b);
    }

    public static LagrangeBasis operator *(in LagrangeBasis a, in FrE b)
    {
        return Scale(a, b);
    }

    public static LagrangeBasis operator *(in FrE a, in LagrangeBasis b)
    {
        return Scale(b, a);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowVanishingPolynomialEvaluatedToZeroException() => throw new InvalidOperationException(
        "vanishing polynomial evaluated to zero. z is therefore a point on the domain");

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowVanishingPolyLengthException() =>
        throw new InvalidConstraintException("root.Length() != ys.Length + 1");
}
