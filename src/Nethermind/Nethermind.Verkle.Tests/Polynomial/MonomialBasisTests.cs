using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;

namespace Nethermind.Verkle.Tests.Polynomial;

public class MonomialBasisTests
{
    [Test]
    public void TestVanishingPoly()
    {
        FrE[] xs =
        {
            FrE.SetElement(), FrE.SetElement(1), FrE.SetElement(2), FrE.SetElement(3), FrE.SetElement(4),
            FrE.SetElement(5)
        };

        MonomialBasis z = MonomialBasis.VanishingPoly(xs);

        foreach (FrE x in xs) Assert.That(z.Evaluate(x).IsZero);
    }

    [Test]
    public void TestPolyDivision()
    {
        FrE[] aL = { FrE.SetElement(2), FrE.SetElement(3), FrE.SetElement(1) };
        MonomialBasis a = new(aL);
        FrE[] bL = { FrE.SetElement(1), FrE.SetElement(1) };
        MonomialBasis b = new(bL);

        MonomialBasis result = a / b;
        Assert.Multiple(() =>
        {
            Assert.That(result.Coeffs[0], Is.EqualTo(FrE.SetElement(2)));
            Assert.That(result.Coeffs[1], Is.EqualTo(FrE.SetElement(1)));
        });
    }

    [Test]
    public void TestDerivative()
    {
        FrE[] aL = { FrE.SetElement(9), FrE.SetElement(20), FrE.SetElement(10), FrE.SetElement(5), FrE.SetElement(6) };
        MonomialBasis a = new(aL);
        FrE[] bL = { FrE.SetElement(20), FrE.SetElement(20), FrE.SetElement(15), FrE.SetElement(24) };
        MonomialBasis b = new(bL);

        MonomialBasis gotAPrime = MonomialBasis.FormalDerivative(a);
        for (int i = 0; i < gotAPrime.Length(); i++) Assert.That(b.Coeffs[i], Is.EqualTo(gotAPrime.Coeffs[i]));
    }
}
