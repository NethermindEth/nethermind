// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;
        public static long CalculateCost(IReleaseSpec spec, int byteCodeLength) =>
            spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize
                ? long.MaxValue
                : GasCostOf.CodeDeposit * byteCodeLength;

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !IsValid(spec, code.Span);

        public static bool IsValid(IReleaseSpec spec, ReadOnlySpan<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);
    }
}
