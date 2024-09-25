using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;

namespace Nethermind.Verkle.Tests.Polynomial;

public class LagrangeBasisTests
{
    [Test]
    public void TestAddSub()
    {
        FrE[] domainSq =
        [
            FrE.SetElement(), FrE.SetElement(1), FrE.SetElement(4), FrE.SetElement(9), FrE.SetElement(16),
            FrE.SetElement(25)
        ];

        FrE[] domain2 =
        [
            FrE.SetElement(2), FrE.SetElement(3), FrE.SetElement(4), FrE.SetElement(5), FrE.SetElement(6),
            FrE.SetElement(7)
        ];

        LagrangeBasis a = new(domainSq);
        LagrangeBasis b = new(domain2);

        FrE[] expected =
        [
            FrE.SetElement(2), FrE.SetElement(4), FrE.SetElement(8), FrE.SetElement(14), FrE.SetElement(22),
            FrE.SetElement(32)
        ];
        LagrangeBasis ex = new(expected);
        LagrangeBasis result = a + b;

        for (int i = 0; i < ex.Evaluations.Length; i++)
            Assert.That(ex.Evaluations[i], Is.EqualTo(result.Evaluations[i]));

        ex -= b;
        for (int i = 0; i < ex.Evaluations.Length; i++) Assert.That(ex.Evaluations[i], Is.EqualTo(a.Evaluations[i]));
    }

    [Test]
    public void TestMul()
    {
        FrE[] domainSq =
        [
            FrE.SetElement(), FrE.SetElement(1), FrE.SetElement(4), FrE.SetElement(9), FrE.SetElement(16),
            FrE.SetElement(25)
        ];
        FrE[] domainPow4 =
        [
            FrE.SetElement(), FrE.SetElement(1), FrE.SetElement(16), FrE.SetElement(81), FrE.SetElement(256),
            FrE.SetElement(625)
        ];


        LagrangeBasis a = new(domainSq);
        LagrangeBasis result = a * a;

        LagrangeBasis ex = new(domainPow4);

        for (int i = 0; i < ex.Evaluations.Length; i++)
            Assert.That(ex.Evaluations[i], Is.EqualTo(result.Evaluations[i]));
    }

    [Test]
    public void TestScale()
    {
        FrE[] domainSq =
        [
            FrE.SetElement(), FrE.SetElement(1), FrE.SetElement(4), FrE.SetElement(9), FrE.SetElement(16),
            FrE.SetElement(25)
        ];

        FrE constant = FrE.SetElement(10);

        LagrangeBasis a = new(domainSq);
        LagrangeBasis result = a * constant;

        FrE[] expected =
        [
            FrE.SetElement(), FrE.SetElement(10), FrE.SetElement(40), FrE.SetElement(90), FrE.SetElement(160),
            FrE.SetElement(250)
        ];
        LagrangeBasis ex = new(expected);

        for (int i = 0; i < ex.Evaluations.Length; i++)
            Assert.That(ex.Evaluations[i], Is.EqualTo(result.Evaluations[i]));
    }

    // [Test]
    // public void TestInterpolation()
    // {
    //     FrE[] domainSq =
    //     {
    //         FrE.SetElement(), FrE.SetElement(1), FrE.SetElement(4), FrE.SetElement(9), FrE.SetElement(16),
    //         FrE.SetElement(25)
    //     };
    //
    //     LagrangeBasis xSquaredLagrange = new(domainSq);
    //     MonomialBasis xSquaredCoeff = xSquaredLagrange.Interpolate();
    //
    //     MonomialBasis expectedXSquaredCoeff = new(
    //         new[] { FrE.Zero, FrE.Zero, FrE.One });
    //
    //     for (int i = 0; i < expectedXSquaredCoeff.Coeffs.Length; i++)
    //         Assert.That(expectedXSquaredCoeff.Coeffs[i], Is.EqualTo(xSquaredCoeff.Coeffs[i]));
    // }
}
