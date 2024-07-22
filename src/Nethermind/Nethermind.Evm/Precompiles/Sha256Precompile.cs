// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class Sha256Precompile : IPrecompile<Sha256Precompile>
    {
        private static readonly ThreadLocal<SHA256> _sha256 = new();

        public static readonly Sha256Precompile Instance = new Sha256Precompile();

        private Sha256Precompile()
        {
            InitIfNeeded();
        }

        private static void InitIfNeeded()
        {
            if (!_sha256.IsValueCreated)
            {
                var sha = SHA256.Create();
                sha.Initialize();
                _sha256.Value = sha;
            }
        }

        public static Address Address { get; } = Address.FromNumber(2);

        public long BaseGasCost(IReleaseSpec releaseSpec) => 60L;

        public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 12L * EvmPooledMemory.Div32Ceiling((ulong)inputData.Length);

        public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Metrics.Sha256Precompile++;
            InitIfNeeded();

            byte[] output = new byte[SHA256.HashSizeInBytes];
            bool success = _sha256.Value.TryComputeHash(inputData.Span, output, out int bytesWritten);

            return (output, success && bytesWritten == SHA256.HashSizeInBytes);
        }
    }
}
