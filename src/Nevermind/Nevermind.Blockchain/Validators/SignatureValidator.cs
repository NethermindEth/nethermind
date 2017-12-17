using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using Nevermind.Core.Potocol;

namespace Nevermind.Blockchain.Validators
{
    public class SignatureValidator : ISignatureValidator
    {
        private readonly int _chainIdValue;

        private readonly IProtocolSpecification _spec;

        public SignatureValidator(IProtocolSpecification spec, int chainIdValue)
        {
            _spec = spec;
            _chainIdValue = chainIdValue;
        }

        public SignatureValidator(IProtocolSpecification spec, ChainId chainId)
            : this(spec, (int)chainId)
        {
        }

        public bool Validate(Signature signature)
        {
            BigInteger sValue = signature.S.ToUnsignedBigInteger();
            BigInteger rValue = signature.R.ToUnsignedBigInteger();

            if (_chainIdValue < 0)
            {
                return false;
            }

            if (sValue.IsZero || rValue.IsZero)
            {
                return false;
            }

            if (sValue >= (_spec.IsEip2Enabled ? Secp256K1Curve.HalfN + 1 : Secp256K1Curve.N))
            {
                return false;
            }

            if (rValue >= Secp256K1Curve.N - 1)
            {
                return false;
            }

            if (!_spec.IsEip155Enabled)
            {
                return signature.V == 27 || signature.V == 28;
            }

            return signature.V == 27 || signature.V == 28 || signature.V == 37 || signature.V == 38;
            // TODO: transaction tests only allow 37 / 38 (Chain ID 1)
            //return signature.V == 27 || signature.V == 28 || signature.V == 35 + 2 * _chainIdValue || signature.V == 36 + 2 * _chainIdValue;
        }
    }
}