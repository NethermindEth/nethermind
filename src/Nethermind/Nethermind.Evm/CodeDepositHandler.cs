// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;

        public static ulong CalculateCost(IReleaseSpec spec, int byteCodeLength) =>
            CalculateCost(spec, byteCodeLength, out ulong regularCost, out long stateCost)
                ? regularCost + (ulong)stateCost
                : ulong.MaxValue;

        public static ulong CalculateCost<TGasPolicy>(IReleaseSpec spec, int byteCodeLength, in TGasPolicy gas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            CalculateCost(spec, byteCodeLength, in gas, out ulong regularCost, out long stateCost)
                ? regularCost + (ulong)stateCost
                : ulong.MaxValue;

        public static bool CalculateCost(IReleaseSpec spec, int byteCodeLength, out ulong regularCost, out long stateCost)
        {
            stateCost = 0;

            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
            {
                regularCost = ulong.MaxValue;
                return false;
            }

            ulong length = (ulong)byteCodeLength;
            if (!spec.IsEip8037Enabled)
            {
                regularCost = GasCostOf.CodeDeposit * length;
                return true;
            }

            ulong words = EvmCalculations.Div32Ceiling(length, out bool outOfGas);
            if (outOfGas)
            {
                regularCost = ulong.MaxValue;
                stateCost = long.MaxValue;
                return false;
            }

            regularCost = GasCostOf.CodeDepositRegularPerWord * words;
            stateCost = GasCostOf.CodeDepositState * byteCodeLength;
            return true;
        }

        public static bool CalculateCost<TGasPolicy>(IReleaseSpec spec, int byteCodeLength, in TGasPolicy gas, out ulong regularCost, out long stateCost)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        {
            stateCost = 0;

            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
            {
                regularCost = ulong.MaxValue;
                return false;
            }

            ulong length = (ulong)byteCodeLength;
            if (!spec.IsEip8037Enabled)
            {
                regularCost = GasCostOf.CodeDeposit * length;
                return true;
            }

            ulong words = EvmCalculations.Div32Ceiling(length, out bool outOfGas);
            if (outOfGas)
            {
                regularCost = ulong.MaxValue;
                stateCost = long.MaxValue;
                return false;
            }

            regularCost = GasCostOf.CodeDepositRegularPerWord * words;
            stateCost = TGasPolicy.GetCodeDepositStateCost(byteCodeLength);
            return true;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !CodeIsValid(spec, code);

        public static bool CodeIsValid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);
    }
}
