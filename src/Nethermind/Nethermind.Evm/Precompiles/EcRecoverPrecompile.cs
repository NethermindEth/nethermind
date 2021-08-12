//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Evm.Precompiles
{
    public class EcRecoverPrecompile : IPrecompile
    {
        public static readonly IPrecompile Instance = new EcRecoverPrecompile();

        private EcRecoverPrecompile()
        {
        }

        public Address Address { get; } = Address.FromNumber(1);

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 3000L;
        }

        private readonly EthereumEcdsa _ecdsa = new(ChainId.Mainnet, LimboLogs.Instance);
        
        private readonly byte[] _zero31 = new byte[31];
        
        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.EcRecoverPrecompile++;
            
            Span<byte> inputDataSpan = stackalloc byte[128];
            inputData.Span.Slice(0, Math.Min(128, inputData.Length))
                .CopyTo(inputDataSpan.Slice(0, Math.Min(128, inputData.Length)));

            Keccak hash = new(inputDataSpan.Slice(0, 32).ToArray());
            Span<byte> vBytes = inputDataSpan.Slice(32, 32);
            Span<byte> r = inputDataSpan.Slice(64, 32);
            Span<byte> s = inputDataSpan.Slice(96, 32);

            // TEST: CALLCODEEcrecoverV_prefixedf0_d0g0v0
            // TEST: CALLCODEEcrecoverV_prefixedf0_d1g0v0
            if (!Bytes.AreEqual(_zero31, vBytes.Slice(0, 31)))
            {
                return (Array.Empty<byte>(), true);
            }
            
            byte v = vBytes[31];
            if (v != 27 && v != 28)
            {
                return (Array.Empty<byte>(), true);
            }

            Signature signature = new(r, s, v);
            Address recovered = _ecdsa.RecoverAddress(signature, hash);
            if (recovered == null)
            {
                return (Array.Empty<byte>(), true);
            }

            byte[] result = recovered.Bytes;
            if (result.Length != 32)
            {
                result = result.PadLeft(32);
            }
            
            // TODO: change recovery code to return bytes
            return (result, true);
        }
    }
}
