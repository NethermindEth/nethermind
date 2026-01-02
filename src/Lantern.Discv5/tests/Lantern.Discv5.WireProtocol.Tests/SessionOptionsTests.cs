using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Utility;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

public class SessionOptionsTests
{
    private SessionOptions _sessionOptions = null!;

    [Test]
    public void Test_SessionOptions_CreateDefault()
    {
        _sessionOptions = SessionOptions.Default;

        Assert.NotNull(_sessionOptions);
        Assert.NotNull(_sessionOptions.Signer);
        Assert.NotNull(_sessionOptions.Verifier);
        Assert.NotNull(_sessionOptions.SessionKeys);
        Assert.AreEqual(1000, _sessionOptions.SessionCacheSize);
    }

    [Test]
    public void Test_SessionOptions_Builder()
    {
        var privateKey = RandomUtility.GenerateRandomData(32);
        var signer = new IdentitySignerV4(privateKey);
        var verifier = new IdentityVerifierV4();
        var sessionKeys = new SessionKeys(privateKey);

        _sessionOptions = new SessionOptions
        {
            Signer = signer,
            Verifier = verifier,
            SessionKeys = sessionKeys,
            SessionCacheSize = 2000
        };

        Assert.NotNull(_sessionOptions);
        Assert.NotNull(_sessionOptions.Signer);
        Assert.NotNull(_sessionOptions.Verifier);
        Assert.NotNull(_sessionOptions.SessionKeys);
        Assert.AreEqual(signer, _sessionOptions.Signer);
        Assert.AreEqual(verifier, _sessionOptions.Verifier);
        Assert.AreEqual(sessionKeys, _sessionOptions.SessionKeys);
        Assert.AreEqual(2000, _sessionOptions.SessionCacheSize);
    }
}
