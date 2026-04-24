// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;

namespace Nethermind.Core
{
    public static class GasCostOf
    {
        private const ulong StateGrowthTargetBytes = 100UL * 1024 * 1024 * 1024;
        private const ulong BlocksPerYear = 2_628_000;
        private const int CostPerStateByteSigBits = 5;
        private const ulong CostPerStateByteOffset = 9_578;
        private const ulong CostPerStateByteDivisor = 2 * StateGrowthTargetBytes;

        public const long Free = 0;
        public const long Base = 2;
        public const long VeryLow = 3;
        public const long Low = 5;
        public const long Mid = 8;
        public const long High = 10;
        public const long Jump = Mid;
        public const long JumpI = High;
        public const long ExtCode = 20;
        public const long ExtCodeEip150 = 700;
        public const long Balance = 20;
        public const long BalanceEip150 = 400;
        public const long BalanceEip1884 = 700;
        public const long SLoad = 50;
        public const long SLoadEip150 = 200;
        public const long SLoadEip1884 = 800;
        public const long JumpDest = 1;
        public const long SStoreNetMeteredEip1283 = 200;
        public const long SStoreNetMeteredEip2200 = 800;
        public const long SSet = 20000;
        public const long SReset = 5000;
        public const long Create = 32000;
        public const long CodeDeposit = 200;
        public const long Call = 40;
        public const long CallEip150 = 700;
        public const long CallValue = 9000;
        public const long CallStipend = 2300;
        public const long NewAccount = 25000;
        public const long Exp = 10;
        public const long ExpByte = 10;
        public const long ExpByteEip160 = 50;
        public const long Memory = 3;
        public const long TxCreate = 32000;
        public const long TxDataZero = 4;
        public const long TxDataNonZero = 68;
        public const long TxDataNonZeroEip2028 = 16;
        public const long Transaction = 21000;
        public const long BlobHash = 3;
        public const long Log = 375;
        public const long LogTopic = 375;
        public const long LogData = 8;
        public const long Sha3 = 30;
        public const long Sha3Word = 6;
        public const long BlockHash = 20;
        public const long SelfDestruct = 0;
        public const long SelfDestructEip150 = 5000;
        public const long ExtCodeHash = 400;
        public const long ExtCodeHashEip1884 = 700;
        public const long SelfBalance = 5;
        public const long InitCodeWord = 2; //eip-3860 gas per word cost for init code size

        public const long ColdSLoad = 2100; // eip-2929

        public const long ColdAccountAccess = 2600; // eip-2929
        public const long WarmStateRead = 100; // eip-2929
        public const long CallPrecompileEip2929 = 100; // eip-2929

        public const long AccessAccountListEntry = 2400; // eip-2930
        public const long AccessStorageListEntry = 1900; // eip-2930
        public const long TLoad = WarmStateRead; // eip-1153
        public const long TStore = WarmStateRead; // eip-1153
        public const long PerAuthBaseCost = 12500; // eip-7702
        public const long TotalCostFloorPerTokenEip7623 = 10; // eip-7623
        public const long TotalCostFloorPerTokenEip7976 = 16; // eip-7976

        // EIP-8037 default/fallback values for 100M block gas limit.
        public const long CostPerStateByte = 1174;
        public const long SSetRegular = 2_900;
        public const long SSetState = 32 * CostPerStateByte;
        public const long CreateRegular = 9_000;
        public const long CreateState = 112 * CostPerStateByte;
        public const long NewAccountState = 112 * CostPerStateByte;
        public const long CodeDepositRegularPerWord = 6;
        public const long CodeDepositState = CostPerStateByte;
        public const long PerAuthBaseRegular = 7_500;
        public const long PerAuthBaseState = 23 * CostPerStateByte;
        public const long PerEmptyAccountState = 112 * CostPerStateByte;
        public const long BlockAccessListItem = 2_000; // eip-7928

        public const long TxDataNonZeroMultiplier = TxDataNonZero / TxDataZero;
        public const long TxDataNonZeroMultiplierEip2028 = TxDataNonZeroEip2028 / TxDataZero;

        public const long MinModExpEip2565 = 200; // eip-2565
        public const long MinModExpEip7883 = 500; // eip-7883

        public static long CalculateCostPerStateByte(long blockGasLimit)
        {
            if (blockGasLimit <= 0)
            {
                return CostPerStateByte;
            }

            UInt128 scaledGasLimit = (ulong)blockGasLimit;
            UInt128 raw = (scaledGasLimit * BlocksPerYear + CostPerStateByteDivisor - 1) / CostPerStateByteDivisor;
            ulong shifted = (ulong)raw + CostPerStateByteOffset;
            ulong quantized = QuantizeToSignificantBits(shifted, CostPerStateByteSigBits);
            return quantized <= CostPerStateByteOffset
                ? 1
                : (long)(quantized - CostPerStateByteOffset);
        }

        public static long CalculateSSetState(long costPerStateByte) => checked(32 * costPerStateByte);
        public static long CalculateCreateState(long costPerStateByte) => checked(112 * costPerStateByte);
        public static long CalculateNewAccountState(long costPerStateByte) => checked(112 * costPerStateByte);
        public static long CalculateCodeDepositState(long costPerStateByte, int byteCodeLength) => checked(costPerStateByte * byteCodeLength);
        public static long CalculatePerAuthBaseState(long costPerStateByte) => checked(23 * costPerStateByte);
        public static long CalculatePerEmptyAccountState(long costPerStateByte) => checked(112 * costPerStateByte);
        public static long CalculateSSetReversalRefund(long costPerStateByte) => checked(CalculateSSetState(costPerStateByte) + SSetRegular - WarmStateRead);

        private static ulong QuantizeToSignificantBits(ulong value, int significantBits)
        {
            if (value == 0)
            {
                return 0;
            }

            int shift = Math.Max(BitOperations.Log2(value) + 1 - significantBits, 0);
            return (value >> shift) << shift;
        }
    }
}
