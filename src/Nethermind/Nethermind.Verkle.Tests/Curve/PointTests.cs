using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Tests.Curve;

public class PointTests
{
    [Test]
    public void TestAddition()
    {
        ExtendedPoint gen = ExtendedPoint.Generator;
        ExtendedPoint resultAdd = gen + gen;

        ExtendedPoint resultDouble = ExtendedPoint.Double(gen);

        Assert.That(resultAdd == resultDouble);
    }

    [Test]
    public void TestEq()
    {
        ExtendedPoint gen = ExtendedPoint.Generator;
        ExtendedPoint gen2 = ExtendedPoint.Generator;

        ExtendedPoint negGen = ExtendedPoint.Neg(gen);

        Assert.That(gen == gen2);
        Assert.That(gen != negGen);
    }

    [Test]
    public void TestNeg()
    {
        ExtendedPoint gen = ExtendedPoint.Generator;
        ExtendedPoint expected = ExtendedPoint.Identity;
        ExtendedPoint result = gen + ExtendedPoint.Neg(gen);

        Assert.That(expected == result);
    }

    [Test]
    public void TestSerialiseGen()
    {
        ExtendedPoint gen = ExtendedPoint.Generator;

        byte[]? serialized = gen.ToBytes();
        const string expected = "18ae52a26618e7e1658499ad22c0792bf342be7b77113774c5340b2ccc32c129";
        Convert.ToHexString(serialized).Should().BeEquivalentTo(expected);
    }

    [Test]
    public void TestScalarMulSmoke()
    {
        ExtendedPoint gen = ExtendedPoint.Generator;
        FrE scalar = FrE.SetElement(2);
        ExtendedPoint result = gen * scalar;
        ExtendedPoint twoGen = ExtendedPoint.Double(gen);
        Assert.That(twoGen == result);
    }

    [Test]
    public void TestScalarMulMinusOne()
    {
        ExtendedPoint gen = ExtendedPoint.Generator;

        const int x = -1;
        FrE scalar = FrE.SetElement(x);
        ExtendedPoint result = gen * scalar;
        byte[]? serialized = result.ToBytes();
        const string expected = "e951ad5d98e7181e99d76452e0e343281295e38d90c602bf824892fd86742c4a";
        Convert.ToHexString(serialized).Should().BeEquivalentTo(expected);
    }
}
