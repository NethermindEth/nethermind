using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Utility;

namespace Lantern.Discv5.WireProtocol.Session;

public class SessionOptions
{
    public IIdentitySigner? Signer { get; set; }
    public IIdentityVerifier? Verifier { get; set; }
    public ISessionKeys? SessionKeys { get; set; }
    public int SessionCacheSize { get; set; } = 1000;

    public static SessionOptions Default
    {
        get
        {
            var privateKey = RandomUtility.GenerateRandomData(32);
            var signer = new IdentitySignerV4(privateKey);
            var verifier = new IdentityVerifierV4();
            var sessionKeys = new SessionKeys(privateKey);

            return new SessionOptions
            {
                Signer = signer,
                Verifier = verifier,
                SessionKeys = sessionKeys
            };
        }
    }
}
