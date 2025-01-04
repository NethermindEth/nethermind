// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class TxIndexPrecompile : IPrecompile<TxIndexPrecompile>
    {
        public static TxIndexPrecompile Instance { get; } = new();

        public static Address Address => new("0x0000000000000000000000000000000000000015");

        private TxIndexPrecompile() { }

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 2;
        }

        public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            return 0;
        }

        public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, PrecompileContext context)
        {
            ulong txIndex = context.ExecutionEnvironment.TxExecutionContext.Index;

            byte[] result = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                result[31 - i] = (byte)(txIndex & 0xFF);
                txIndex >>= 8;
            }

            return (result, true);
        }
    }
}
