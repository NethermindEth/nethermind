using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Polynomial;

public class MonomialBasis
{
    public readonly FrE[] Coeffs;

    public MonomialBasis(FrE[] coeffs)
    {
        Coeffs = coeffs;
    }

    public static MonomialBasis Empty()
    {
        return new MonomialBasis(Array.Empty<FrE>());
    }

    private static MonomialBasis Mul(MonomialBasis a, MonomialBasis b)
    {
        FrE[] output = new FrE[a.Length() + b.Length() - 1];
        for (int i = 0; i < a.Length(); i++)
        for (int j = 0; j < b.Length(); j++)
            output[i + j] += a.Coeffs[i]! * b.Coeffs[j]!;

        return new MonomialBasis(output);
    }

    private static MonomialBasis Div(MonomialBasis a, MonomialBasis b)
    {
        if (a.Length() < b.Length()) ThrowLengthConstraintException();

        FrE[] x = a.Coeffs.ToArray();
        List<FrE> output = [];

        int aPos = a.Length() - 1;
        int bPos = b.Length() - 1;

        int diff = aPos - bPos;
        while (diff >= 0)
        {
            FrE quot = x[aPos]! / b.Coeffs[bPos]!;
            output.Insert(0, quot!);
            for (int i = bPos; i > -1; i--) x[diff + i] -= b.Coeffs[i] * quot;

            aPos -= 1;
            diff -= 1;
        }

        return new MonomialBasis(output.ToArray());
    }

    public FrE Evaluate(FrE x)
    {
        FrE y = FrE.Zero;
        FrE powerOfX = FrE.One;
        foreach (FrE pCoeff in Coeffs)
        {
            FrE.MultiplyMod(in powerOfX, in pCoeff, out FrE eval);
            FrE.AddMod(in y, in eval, out y);
            FrE.MultiplyMod(in powerOfX, in x, out powerOfX);
        }

        return y;
    }

    public static MonomialBasis FormalDerivative(MonomialBasis f)
    {
        FrE[] derivative = new FrE[f.Length() - 1];
        for (int i = 1; i < f.Length(); i++)
        {
            FrE x = FrE.SetElement(i) * f.Coeffs[i]!;
            derivative[i - 1] = x;
        }

        return new MonomialBasis(derivative.ToArray());
    }

    public static MonomialBasis VanishingPoly(IEnumerable<FrE> xs)
    {
        List<FrE> root = [FrE.One];
        foreach (FrE x in xs)
        {
            root.Insert(0, FrE.Zero);
            for (int i = 0; i < root.Count - 1; i++) root[i] -= root[i + 1] * x;
        }

        return new MonomialBasis(root.ToArray());
    }

    public int Length()
    {
        return Coeffs.Length;
    }

    public static MonomialBasis operator /(in MonomialBasis a, in MonomialBasis b)
    {
        return Div(a, b);
    }

    public static MonomialBasis operator *(in MonomialBasis a, in MonomialBasis b)
    {
        return Mul(a, b);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowLengthConstraintException() =>
        throw new InvalidConstraintException("Both operands must be of same length");
}
