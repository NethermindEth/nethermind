using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC;

namespace Nevermind.Core
{
    // ReSharper disable once InconsistentNaming
    public static class EC
    {
        public const string CurveName = "secp256k1";

        public static readonly X9ECParameters CurveParameters = SecNamedCurves.GetByName(CurveName);

        public static ECCurve Curve => DomainParameters.Curve;

        public static readonly ECDomainParameters DomainParameters =
            new ECDomainParameters(CurveParameters.Curve, CurveParameters.G, CurveParameters.N, CurveParameters.H);
    }
}