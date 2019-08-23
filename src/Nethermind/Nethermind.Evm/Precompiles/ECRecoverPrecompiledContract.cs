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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Evm.Precompiles
{
    public class EcRecoverPrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new EcRecoverPrecompiledContract();

        private EcRecoverPrecompiledContract()
        {
        }

        public Address Address { get; } = Address.FromNumber(1);

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 3000L;
        }

        private readonly EthereumEcdsa _ecdsa = new EthereumEcdsa(OlympicSpecProvider.Instance, NullLogManager.Instance);
        
        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.EcRecoverPrecompile++;
            
            inputData = (inputData ?? Bytes.Empty).PadRight(128);

            Keccak hash = new Keccak(inputData.Slice(0, 32));
            byte[] vBytes = inputData.Slice(32, 32);
            byte[] r = inputData.Slice(64, 32);
            byte[] s = inputData.Slice(96, 32);

            // TEST: CALLCODEEcrecoverV_prefixedf0_d0g0v0
            // TEST: CALLCODEEcrecoverV_prefixedf0_d1g0v0
            for (int i = 0; i < 31; i++)
            {
                if (vBytes[i] != 0)
                {
                    return (Bytes.Empty, true);
                }
            }

            byte v = vBytes[31];
            if (v != 27 && v != 28)
            {
                return (Bytes.Empty, true);
            }

            Signature signature = new Signature(r, s, v);
            Address recovered = _ecdsa.RecoverAddress(signature, hash);
            if (recovered == null)
            {
                return (Bytes.Empty, true);
            }
            
            return (recovered.Bytes.PadLeft(32), true); // TODO: change recovery code to return bytes?
        }
    }
}