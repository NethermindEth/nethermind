using Lantern.Discv5.WireProtocol.Session;
using NBitcoin.Secp256k1;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class CryptoSessionTests
{
    private static readonly SessionCrypto SessionCrypto = new();

    [Test]
    public void Test_Ecdh_ShouldGenerateSharedSecretCorrectly()
    {
        var publicKey = Convert.FromHexString("039961e4c2356d61bedb83052c115d311acb3a96f5777296dcf297351130266231");
        var secretKey = Convert.FromHexString("fb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736");
        var expectedSharedSecret =
            Convert.FromHexString("033b11a2a1f214567e1537ce5e509ffd9b21373247f2a3ff6841f4976f53165e7e");
        var sharedSecret = SessionCrypto.GenerateSharedSecret(secretKey, publicKey, Context.Instance);
        Assert.IsTrue(sharedSecret.SequenceEqual(expectedSharedSecret));
    }

    [Test]
    public void Test_InitiatorAndRecipientKeyGeneration_ShouldGenerateCorrectly()
    {
        var ephemeralKey = Convert.FromHexString("fb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736");
        var destPubkey = Convert.FromHexString("0317931e6e0840220642f230037d285d122bc59063221ef3226b1f403ddc69ca91");
        var nodeIdA = Convert.FromHexString("aaaa8419e9f49d0083561b48287df592939a8d19947d8c0ef88f2a4856a69fbb");
        var nodeIdB = Convert.FromHexString("bbbb9d047f0488c0b5a93c1c3f2d8bafc7c8ff337024a55434a0d0555de64db9");
        var challengeData =
            Convert.FromHexString(
                "000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000");
        var sharedSecret = SessionCrypto.GenerateSharedSecret(ephemeralKey, destPubkey, Context.Instance);
        var initiatorKey = SessionCrypto.GenerateSessionKeys(sharedSecret, nodeIdA, nodeIdB, challengeData).InitiatorKey;
        var recipientKey = SessionCrypto.GenerateSessionKeys(sharedSecret, nodeIdA, nodeIdB, challengeData).RecipientKey;
        Assert.IsTrue(initiatorKey.SequenceEqual(Convert.FromHexString("dccc82d81bd610f4f76d3ebe97a40571")));
        Assert.IsTrue(recipientKey.SequenceEqual(Convert.FromHexString("ac74bb8773749920b0d3a8881c173ec5")));
    }

    [Test]
    public void Test_IdSignatureGeneration_ShouldCreateSignatureCorrectly()
    {
        var sessionKeys =
            new SessionKeys(Convert.FromHexString("fb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736"));
        var challengeData = Convert.FromHexString("000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000");
        var ephemeralPubkey =
            Convert.FromHexString("039961e4c2356d61bedb83052c115d311acb3a96f5777296dcf297351130266231");
        var nodeBId = Convert.FromHexString("bbbb9d047f0488c0b5a93c1c3f2d8bafc7c8ff337024a55434a0d0555de64db9");
        var signature = SessionCrypto.GenerateIdSignature(sessionKeys, challengeData, ephemeralPubkey, nodeBId);
        var expectedSignature = Convert.FromHexString("94852a1e2318c4e5e9d422c98eaf19d1d90d876b29cd06ca7cb7546d0fff7b484fe86c09a064fe72bdbef73ba8e9c34df0cd2b53e9d65528c2c7f336d5dfc6e6");
        Assert.IsTrue(signature.SequenceEqual(expectedSignature));
    }

    [Test]
    public void Test_IdSignatureVerification_ShouldVerifySignatureCorrectly()
    {
        var sessionKeys = new SessionKeys(Convert.FromHexString("fb757dc581730490a1d7a00deea65e9b1936924caaea8f44d476014856b68736"));
        var challengeData = Convert.FromHexString("000000000000000000000000000000006469736376350001010102030405060708090a0b0c00180102030405060708090a0b0c0d0e0f100000000000000000");
        var ephemeralPubkey =
            Convert.FromHexString("039961e4c2356d61bedb83052c115d311acb3a96f5777296dcf297351130266231");
        var nodeBId = Convert.FromHexString("bbbb9d047f0488c0b5a93c1c3f2d8bafc7c8ff337024a55434a0d0555de64db9");
        var signature = Convert.FromHexString("94852a1e2318c4e5e9d422c98eaf19d1d90d876b29cd06ca7cb7546d0fff7b484fe86c09a064fe72bdbef73ba8e9c34df0cd2b53e9d65528c2c7f336d5dfc6e6");
        var result = SessionCrypto.VerifyIdSignature(signature, challengeData, sessionKeys.PublicKey, ephemeralPubkey, nodeBId, Context.Instance);
        Assert.AreEqual(true, result);
    }
}
