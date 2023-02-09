// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;
        public static long CalculateCost(int byteCodeLength, IReleaseSpec spec)
        {
            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
                return long.MaxValue;

            return GasCostOf.CodeDeposit * byteCodeLength;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, byte[] output)
        {
            return spec.IsEip3541Enabled && output.Length >= 1 && output[0] == InvalidStartingCodeByte;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> output)
        {
            return spec.IsEip3541Enabled && output.Length >= 1 && output.StartsWith(InvalidStartingCodeByte);
        }
    }
}
