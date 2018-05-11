/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Validators
{
    public class SignatureValidator : ISignatureValidator
    {
        private readonly int _chainIdValue;

        public SignatureValidator(int chainId)
        {
            if (chainId < 0)
            {
                throw new ArgumentException("Unexpected negative value", nameof(chainId));
            }

            _chainIdValue = chainId;
        }

        public bool Validate(Signature signature, IReleaseSpec spec)
        {
            BigInteger sValue = signature.S.ToUnsignedBigInteger();
            BigInteger rValue = signature.R.ToUnsignedBigInteger();
            
            if (sValue.IsZero || sValue >= (spec.IsEip2Enabled ? Secp256K1Curve.HalfN + 1 : Secp256K1Curve.N))
            {
                return false;
            }

            if (rValue.IsZero || rValue >= Secp256K1Curve.N - 1)
            {
                return false;
            }

            if (!spec.IsEip155Enabled)
            {
                return signature.V == 27 || signature.V == 28;
            }

            return signature.V == 27 || signature.V == 28 || signature.V == 35 + 2 * _chainIdValue || signature.V == 36 + 2 * _chainIdValue;
        }
    }
}