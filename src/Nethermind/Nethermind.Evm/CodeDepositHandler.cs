// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;

        public static ulong CalculateCost(IReleaseSpec spec, int byteCodeLength) =>
            CalculateCost(spec, byteCodeLength, out ulong executionCost, out long stateCost)
                ? executionCost + (ulong)stateCost
                : ulong.MaxValue;

        public static ulong CalculateCost<TGasPolicy>(IReleaseSpec spec, int byteCodeLength, in TGasPolicy gas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            CalculateCost(spec, byteCodeLength, in gas, out ulong executionCost, out long stateCost)
                ? executionCost + (ulong)stateCost
                : ulong.MaxValue;

        public static bool CalculateCost(IReleaseSpec spec, int byteCodeLength, out ulong executionCost, out long stateCost)
        {
            stateCost = 0;

            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
            {
                executionCost = ulong.MaxValue;
                return false;
            }

            ulong length = (ulong)byteCodeLength;
            if (!spec.IsEip8037Enabled)
            {
                executionCost = GasCostOf.CodeDeposit * length;
                return true;
            }

            ulong words = EvmCalculations.Div32Ceiling(length, out bool outOfGas);
            if (outOfGas)
            {
                executionCost = ulong.MaxValue;
                stateCost = long.MaxValue;
                return false;
            }

            executionCost = GasCostOf.CodeDepositExecutionPerWord * words;
            stateCost = GasCostOf.CodeDepositState * byteCodeLength;
            return true;
        }

        public static bool CalculateCost<TGasPolicy>(IReleaseSpec spec, int byteCodeLength, in TGasPolicy gas, out ulong executionCost, out long stateCost)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        {
            stateCost = 0;

            if (spec.LimitCodeSize && byteCodeLength > spec.MaxCodeSize)
            {
                executionCost = ulong.MaxValue;
                return false;
            }

            ulong length = (ulong)byteCodeLength;
            if (!spec.IsEip8037Enabled)
            {
                executionCost = GasCostOf.CodeDeposit * length;
                return true;
            }

            ulong words = EvmCalculations.Div32Ceiling(length, out bool outOfGas);
            if (outOfGas)
            {
                executionCost = ulong.MaxValue;
                stateCost = long.MaxValue;
                return false;
            }

            executionCost = GasCostOf.CodeDepositExecutionPerWord * words;
            stateCost = TGasPolicy.GetCodeDepositStateCost(byteCodeLength);
            return true;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !CodeIsValid(spec, code);

        public static bool CodeIsValid(IReleaseSpec spec, ReadOnlyMemory<byte> code)
            => !spec.IsEip3541Enabled || !code.StartsWith(InvalidStartingCodeByte);

        /// <summary>
        /// Whether the initcode of <paramref name="createdAccount"/> adopted code through SETCODEFROM (EIP-8298).
        /// </summary>
        /// <remarks>
        /// When it did, creation completion ignores the initcode's return data: it is not validated, installed or
        /// charged code-deposit gas. Checked only under EIP-8298, the one way a created account gets code early.
        /// </remarks>
        internal static bool HasAdoptedCode(IReleaseSpec spec, IWorldState worldState, Address createdAccount)
            => spec.IsEip8298Enabled && worldState.GetCodeHash(createdAccount) != ValueKeccak.OfAnEmptyString;
    }
}
