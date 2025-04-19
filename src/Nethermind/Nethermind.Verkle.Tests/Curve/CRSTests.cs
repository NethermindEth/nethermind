using System.Security.Cryptography;
using Nethermind.Verkle.Curve;

namespace Nethermind.Verkle.Tests.Curve;

public class CRSTests
{
    [Test]
    public void TestCrsIsConsistent()
    {
        Banderwagon[]? crs = CrsStruct.Generate();
        Assert.That(256 == crs.Length);

        string? gotFirstPoint = Convert.ToHexString(crs[0].ToBytes()).ToLower();
        const string expectedFistPoint = "01587ad1336675eb912550ec2a28eb8923b824b490dd2ba82e48f14590a298a0";
        Assert.That(gotFirstPoint.SequenceEqual(expectedFistPoint));

        string? got256ThPoint = Convert.ToHexString(crs[255].ToBytes()).ToLower();
        const string expected256ThPoint = "3de2be346b539395b0c0de56a5ccca54a317f1b5c80107b0802af9a62276a4d8";
        Assert.That(got256ThPoint.SequenceEqual(expected256ThPoint));

        SHA256? hasher = SHA256.Create();
        List<byte> hashData = new();
        foreach (Banderwagon point in crs) hashData.AddRange(point.ToBytes());

        string? result = Convert.ToHexString(hasher.ComputeHash(hashData.ToArray())).ToLower();

        Assert.That(result.SequenceEqual("1fcaea10bf24f750200e06fa473c76ff0468007291fa548e2d99f09ba9256fdb"));
    }

    [Test]
    public void TestCrsNotGenerator()
    {
        Banderwagon[]? crs = CrsStruct.Generate();
        Banderwagon? generator = Banderwagon.Generator;

        foreach (Banderwagon? point in crs) Assert.That(generator != point);
    }

    [Test]
    public void TestCrsGenerator()
    {
        CRS x = CRS.Generate(256);
        Banderwagon generator = Banderwagon.Generator;

        foreach (Banderwagon? point in x.BasisG) Assert.That(generator != point);

        Banderwagon[] crs = CrsStruct.Generate();

        for (int i = 0; i < 256; i++) Assert.That(x.BasisG[i] == crs[i]);
    }
}
