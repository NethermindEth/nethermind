// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public static Address Address { get; } = Address.FromNumber(1);

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0L;
        }

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 3000L;
        }

        private readonly EthereumEcdsa _ecdsa = new(BlockchainIds.Mainnet, LimboLogs.Instance);

        private readonly byte[] _zero31 = new byte[31];

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.EcRecoverPrecompile++;

            Span<byte> inputDataSpan = stackalloc byte[128];
            inputData.Span[..Math.Min(128, inputData.Length)]
                .CopyTo(inputDataSpan[..Math.Min(128, inputData.Length)]);

            Keccak hash = new(inputDataSpan[..32]);
            Span<byte> vBytes = inputDataSpan.Slice(32, 32);
            Span<byte> r = inputDataSpan.Slice(64, 32);
            Span<byte> s = inputDataSpan.Slice(96, 32);

            // TEST: CALLCODEEcrecoverV_prefixedf0_d0g0v0
            // TEST: CALLCODEEcrecoverV_prefixedf0_d1g0v0
            if (!Bytes.AreEqual(_zero31, vBytes[..31]))
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
            if (recovered is null)
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
