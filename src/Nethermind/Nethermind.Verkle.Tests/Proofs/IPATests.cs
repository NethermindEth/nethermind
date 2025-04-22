using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Verkle.Tests.Proofs;

public class IpaTests
{
    private static readonly PreComputedWeights _weights = PreComputedWeights.Instance;
    private static readonly CRS _crs = CRS.Instance;

    private readonly FrE[] _poly =
    {
        FrE.SetElement(1), FrE.SetElement(2), FrE.SetElement(3), FrE.SetElement(4), FrE.SetElement(5),
        FrE.SetElement(6), FrE.SetElement(7), FrE.SetElement(8), FrE.SetElement(9), FrE.SetElement(10),
        FrE.SetElement(11), FrE.SetElement(12), FrE.SetElement(13), FrE.SetElement(14), FrE.SetElement(15),
        FrE.SetElement(16), FrE.SetElement(17), FrE.SetElement(18), FrE.SetElement(19), FrE.SetElement(20),
        FrE.SetElement(21), FrE.SetElement(22), FrE.SetElement(23), FrE.SetElement(24), FrE.SetElement(25),
        FrE.SetElement(26), FrE.SetElement(27), FrE.SetElement(28), FrE.SetElement(29), FrE.SetElement(30),
        FrE.SetElement(31), FrE.SetElement(32)
    };


    [Test]
    public void TestBasicIpaProof()
    {
        FrE[] domain = new FrE[256];
        for (int i = 0; i < 256; i++) domain[i] = FrE.SetElement(i);

        List<FrE> lagrangePoly = new();

        for (int i = 0; i < 8; i++) lagrangePoly.AddRange(_poly);


        Banderwagon commitment = _crs.Commit(lagrangePoly.ToArray());

        Assert.That(Convert.ToHexString(commitment.ToBytes()).ToLower()
            .SequenceEqual("1b9dff8f5ebbac250d291dfe90e36283a227c64b113c37f1bfb9e7a743cdb128"));

        Transcript proverTranscript = new("test");

        FrE inputPoint = FrE.SetElement(2101);
        FrE[] b = _weights.BarycentricFormulaConstants(inputPoint);
        IpaProverQuery query = new(lagrangePoly.ToArray(), commitment, inputPoint, b);

        List<byte> cache = new();
        foreach (FrE i in lagrangePoly) cache.AddRange(i.ToBytes().ToArray());

        cache.AddRange(commitment.ToBytes());
        cache.AddRange(inputPoint.ToBytes().ToArray());
        foreach (FrE i in b) cache.AddRange(i.ToBytes().ToArray());

        IpaProofStruct proof = Ipa.MakeIpaProof(_crs, proverTranscript, query, out FrE outputPoint);
        FrE pChallenge = proverTranscript.ChallengeScalar("state");

        Assert.That(Convert.ToHexString(pChallenge.ToBytes()).ToLower()
            .SequenceEqual("0a81881cbfd7d7197a54ebd67ed6a68b5867f3c783706675b34ece43e85e7306"));

        Transcript verifierTranscript = new("test");

        IpaVerifierQuery queryX = new(commitment, inputPoint, b, outputPoint, proof);

        bool ok = Ipa.CheckIpaProof(_crs, verifierTranscript, queryX);

        Assert.That(ok);
    }

    [Test]
    public void TestIpaProofCreateVerify()
    {
        FrE[] lagrangePoly = new FrE[256];
        for (int i = 0; i < 256; i++)
        {
            lagrangePoly[i] = FrE.Zero;
        }
        for (int i = 1; i < 15; i++)
        {
            lagrangePoly[i - 1] = FrE.SetElement(i);
        }

        Banderwagon commitment = _crs.Commit(lagrangePoly);

        Transcript proverTranscript = new("ipa");

        FrE inputPoint = FrE.SetElement(123456789);
        FrE[] b = _weights.BarycentricFormulaConstants(inputPoint);
        IpaProverQuery query = new(lagrangePoly.ToArray(), commitment, inputPoint, b);

        IpaProofStruct proof = Ipa.MakeIpaProof(_crs, proverTranscript, query, out FrE outputPoint);
        Console.WriteLine(Convert.ToHexString(proof.Encode()));

        Transcript verifierTranscript = new("ipa");

        IpaVerifierQuery queryX = new(commitment, inputPoint, b, outputPoint, proof);

        bool ok = Ipa.CheckIpaProof(_crs, verifierTranscript, queryX);

        Assert.That(ok);
    }

    [Test]
    public void TestInnerProduct()
    {
        FrE[] a = { FrE.SetElement(1), FrE.SetElement(2), FrE.SetElement(3), FrE.SetElement(4), FrE.SetElement(5) };

        FrE[] b =
        {
            FrE.SetElement(10), FrE.SetElement(12), FrE.SetElement(13), FrE.SetElement(14), FrE.SetElement(15)
        };

        FrE expectedResult = FrE.SetElement(204);

        FrE gotResult = Ipa.InnerProduct(a, b);
        Assert.That(gotResult, Is.EqualTo(expectedResult));
    }
}
