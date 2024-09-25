using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Polynomial;

public class PreComputedWeights
{
    private const int VerkleNodeWidth = 256;
    public static readonly PreComputedWeights Instance = new();
    private readonly MonomialBasis A;
    public readonly FrE[] APrimeDomain;
    public readonly FrE[] APrimeDomainInv;
    public readonly FrE[] Domain;
    public readonly FrE[] DomainInv;

    private PreComputedWeights()
    {
        Domain = new FrE[VerkleNodeWidth];
        Parallel.For(0, VerkleNodeWidth, i => Domain[i] = FrE.SetElement(i));
        A = MonomialBasis.VanishingPoly(Domain);
        MonomialBasis aPrime = MonomialBasis.FormalDerivative(A);

        APrimeDomain = new FrE[VerkleNodeWidth];
        APrimeDomainInv = new FrE[VerkleNodeWidth];

        for (int i = 0; i < VerkleNodeWidth; i++)
        {
            FrE aPrimeX = aPrime.Evaluate(FrE.SetElement(i));
            FrE.Inverse(in aPrimeX, out FrE aPrimeXInv);
            APrimeDomain[i] = aPrimeX;
            APrimeDomainInv[i] = aPrimeXInv;
        }

        DomainInv = new FrE[(2 * VerkleNodeWidth) - 1];

        int index = 0;
        for (int i = 0; i < VerkleNodeWidth; i++)
        {
            FrE.Inverse(FrE.SetElement(i), out DomainInv[index]);
            index++;
        }

        for (int i = 1 - VerkleNodeWidth; i < 0; i++)
        {
            FrE.Inverse(FrE.SetElement(i), out DomainInv[index]);
            index++;
        }
    }

    public FrE[] BarycentricFormulaConstants(FrE z)
    {
        FrE az = A.Evaluate(z);

        FrE[] elems = new FrE[VerkleNodeWidth];
        for (int i = 0; i < VerkleNodeWidth; i++) elems[i] = z - Domain[i];

        elems = FrE.MultiInverse(elems);
        for (int i = 0; i < VerkleNodeWidth; i++) elems[i] = az * APrimeDomainInv[i] * elems[i];

        return elems;
    }

    public void BarycentricFormulaConstants(in FrE z, in Span<FrE> elems)
    {
        FrE az = A.Evaluate(z);

        for (int i = 0; i < VerkleNodeWidth; i++) elems[i] = z - Domain[i];

        FrE.MultiInverse(elems).CopyTo(elems);
        for (int i = 0; i < VerkleNodeWidth; i++) elems[i] = az * APrimeDomainInv[i] * elems[i];
    }
}
