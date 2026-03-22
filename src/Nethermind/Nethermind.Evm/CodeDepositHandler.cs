// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;

        public static long CalculateCost(IReleaseSpec spec, int byteCodeLength) =>
            CalculateCost(spec, byteCodeLength, out long regularCost, out long stateCost)
                ? regularCost + stateCost
                : long.MaxValue;

        public static bool CalculateCost(IReleaseSpec spec, int byteCodeLength, out long regularCost, out long stateCost)
        {
            stateCost = 0;

            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
            {
                regularCost = long.MaxValue;
                return false;
            }

            if (!spec.IsEip8037Enabled)
            {
                regularCost = GasCostOf.CodeDeposit * byteCodeLength;
                return true;
            }

            long words = EvmCalculations.Div32Ceiling((ulong)byteCodeLength, out bool outOfGas);
            if (outOfGas)
            {
                regularCost = long.MaxValue;
                stateCost = long.MaxValue;
                return false;
            }

            regularCost = GasCostOf.CodeDepositRegularPerWord * words;
            stateCost = GasCostOf.CodeDepositState * byteCodeLength;
            return true;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !CodeIsValid(spec, code);

        public static bool CodeIsValid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);
    }
}
