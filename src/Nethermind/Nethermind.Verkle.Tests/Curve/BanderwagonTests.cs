using System.Diagnostics;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Tests.Curve;

public class BanderwagonTests
{
    [Test]
    public void MSM()
    {
        using IEnumerator<FrE> rand = FrE.GetRandom().GetEnumerator();
        for (int i = 0; i < 10; i++) rand.MoveNext();

        var _a = new FrE[256];
        var res = new List<byte>();

        for (int i = 0; i < 256; i++)
        {
            _a[i] = rand.Current;
            res.AddRange(rand.Current.ToBytes());
            rand.MoveNext();
        }



        Console.WriteLine(res.Count);
        Console.WriteLine(Convert.ToHexString(res.ToArray()));

        Banderwagon data = Banderwagon.Generator;

        for (int i = 0; i < 10; i++)
        {
            data = Banderwagon.MultiScalarMul(CRS.Instance.BasisG, _a);
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            data = Banderwagon.MultiScalarMul(CRS.Instance.BasisG, _a);
        }
        Console.WriteLine($"Elapsed time: {sw.Elapsed} ms");

        Console.WriteLine($"{Convert.ToHexString(data!.ToBytes())}");


    }

    [Test]
    public void MSMRust()
    {
        var context = RustVerkleLib.VerkleContextNew();
        using IEnumerator<FrE> rand = FrE.GetRandom().GetEnumerator();
        for (int i = 0; i < 10; i++) rand.MoveNext();

        var _a = new FrE[256];
        var res = new List<byte>();

        for (int i = 0; i < 256; i++)
        {
            _a[i] = rand.Current;
            res.AddRange(rand.Current.ToBytes());
            rand.MoveNext();
        }

        Console.WriteLine(res.Count);
        Console.WriteLine(Convert.ToHexString(res.ToArray()));
        var outhash = new byte[32];
        for (int i = 0; i < 10; i++)
        {
            RustVerkleLib.MultiScalarMul(context, res.ToArray(), (UIntPtr)res.Count, outhash);
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            RustVerkleLib.MultiScalarMul(context, res.ToArray(), (UIntPtr)res.Count, outhash);
        }
        Console.WriteLine($"Elapsed time: {sw.Elapsed} ms");

        Console.WriteLine($"{Convert.ToHexString(outhash)}");


    }

    [Test]
    public void TestAddition()
    {
        Banderwagon gen = Banderwagon.Generator;
        Banderwagon resultAdd = gen + gen;

        Banderwagon resultDouble = Banderwagon.Double(gen);

        Assert.That(resultAdd == resultDouble);
    }

    [Test]
    public void TestEq()
    {
        Banderwagon gen = Banderwagon.Generator;
        Banderwagon gen2 = Banderwagon.Generator;

        Banderwagon negGen = Banderwagon.Neg(gen);

        Assert.That(gen == gen2);
        Assert.That(gen != negGen);
    }

    [Test]
    public void TestNeg()
    {
        Banderwagon gen = Banderwagon.Generator;
        Banderwagon expected = Banderwagon.Identity;
        Banderwagon result = gen + Banderwagon.Neg(gen);

        Assert.That(expected == result);
    }

    [Test]
    public void TestSerialiseGen()
    {
        Banderwagon gen = Banderwagon.Generator;

        byte[]? serialized = gen.ToBytesUncompressed();
        Banderwagon res = Banderwagon.FromBytesUncompressedUnchecked(serialized);
        Assert.That(res == gen);
    }

    [Test]
    public void TestSerialiseGenLittle()
    {
        Banderwagon gen = Banderwagon.Generator;

        byte[]? serialized = gen.ToBytesUncompressedLittleEndian();
        Banderwagon res = Banderwagon.FromBytesUncompressedUnchecked(serialized, false);
        Assert.That(res == gen);
    }

    [Test]
    public void TestScalarMulSmoke()
    {
        Banderwagon gen = Banderwagon.Generator;
        FrE scalar = FrE.SetElement(2);
        Banderwagon result = gen * scalar;
        Banderwagon twoGen = Banderwagon.Double(gen);
        Assert.That(twoGen == result);
    }

    [Test]
    public void TestScalarMulMinusOne()
    {
        Banderwagon gen = Banderwagon.Generator;

        const int x = -1;
        FrE scalar = FrE.SetElement(x);
        Banderwagon result = gen * scalar;
        byte[] serialized = result.ToBytes();
        const string expected = "29C132CC2C0B34C5743711777BBE42F32B79C022AD998465E1E71866A252AE18";
        Convert.ToHexString(serialized).Should().BeEquivalentTo(expected);
    }

    [Test]
    public void TestSerialiseSmoke()
    {
        string[] expectedBitStrings =
        {
            "4a2c7486fd924882bf02c6908de395122843e3e05264d7991e18e7985dad51e9",
            "43aa74ef706605705989e8fd38df46873b7eae5921fbed115ac9d937399ce4d5",
            "5e5f550494159f38aa54d2ed7f11a7e93e4968617990445cc93ac8e59808c126",
            "0e7e3748db7c5c999a7bcd93d71d671f1f40090423792266f94cb27ca43fce5c",
            "14ddaa48820cb6523b9ae5fe9fe257cbbd1f3d598a28e670a40da5d1159d864a",
            "6989d1c82b2d05c74b62fb0fbdf8843adae62ff720d370e209a7b84e14548a7d",
            "26b8df6fa414bf348a3dc780ea53b70303ce49f3369212dec6fbe4b349b832bf",
            "37e46072db18f038f2cc7d3d5b5d1374c0eb86ca46f869d6a95fc2fb092c0d35",
            "2c1ce64f26e1c772282a6633fac7ca73067ae820637ce348bb2c8477d228dc7d",
            "297ab0f5a8336a7a4e2657ad7a33a66e360fb6e50812d4be3326fab73d6cee07",
            "5b285811efa7a965bd6ef5632151ebf399115fcc8f5b9b8083415ce533cc39ce",
            "1f939fa2fd457b3effb82b25d3fe8ab965f54015f108f8c09d67e696294ab626",
            "3088dcb4d3f4bacd706487648b239e0be3072ed2059d981fe04ce6525af6f1b8",
            "35fbc386a16d0227ff8673bc3760ad6b11009f749bb82d4facaea67f58fc60ed",
            "00f29b4f3255e318438f0a31e058e4c081085426adb0479f14c64985d0b956e0",
            "3fa4384b2fa0ecc3c0582223602921daaa893a97b64bdf94dcaa504e8b7b9e5f"
        };

        List<Banderwagon> points = new();
        Banderwagon point = Banderwagon.Generator;

        foreach (string? bitString in expectedBitStrings)
        {
            byte[] bytes = point.ToBytes();
            Convert.ToHexString(bytes).Should().BeEquivalentTo(bitString);

            points.Add(point);
            point = Banderwagon.Double(point);
        }

        for (int i = 0; i < expectedBitStrings.Length; i++)
        {
            string bitString = expectedBitStrings[i];
            Banderwagon expectedPoint = points[i];
            Banderwagon decodedPoint = new(bitString);
            // Assert.That(point != null);
            Assert.That(decodedPoint == expectedPoint);
        }
    }

    [Test]
    public void TestTwoTorsion()
    {
        Banderwagon gen = Banderwagon.Generator;
        Banderwagon twoTorsion = Banderwagon.TwoTorsionPoint();
        Banderwagon result = Banderwagon.Add(gen, twoTorsion);

        Assert.That(result == gen);
    }
}
