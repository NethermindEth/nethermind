// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public static class GasCostOf
    {
        public const ulong Free = 0;
        public const ulong Base = 2;
        public const ulong VeryLow = 3;
        public const ulong Low = 5;
        public const ulong Mid = 8;
        public const ulong High = 10;
        public const ulong Jump = Mid;
        public const ulong JumpI = High;
        public const ulong ExtCode = 20;
        public const ulong ExtCodeEip150 = 700;
        public const ulong Balance = 20;
        public const ulong BalanceEip150 = 400;
        public const ulong BalanceEip1884 = 700;
        public const ulong SLoad = 50;
        public const ulong SLoadEip150 = 200;
        public const ulong SLoadEip1884 = 800;
        public const ulong JumpDest = 1;
        public const ulong SStoreNetMeteredEip1283 = 200;
        public const ulong SStoreNetMeteredEip2200 = 800;
        public const ulong SSet = 20000;
        public const ulong SReset = 5000;
        public const ulong Create = 32000;
        public const ulong CodeDeposit = 200;
        public const ulong Call = 40;
        public const ulong CallEip150 = 700;
        public const ulong CallValue = 9000;
        public const ulong CallStipend = 2300;
        public const ulong NewAccount = 25000;
        public const ulong Exp = 10;
        public const ulong ExpByte = 10;
        public const ulong ExpByteEip160 = 50;
        public const ulong Memory = 3;
        public const ulong TxCreate = 32000;
        public const ulong TxDataZero = 4;
        public const ulong TxDataNonZero = 68;
        public const ulong TxDataNonZeroEip2028 = 16;
        public const ulong Transaction = 21000;
        public const ulong BlobHash = 3;
        public const ulong Log = 375;
        public const ulong LogTopic = 375;
        public const ulong LogData = 8;
        public const ulong Sha3 = 30;
        public const ulong Sha3Word = 6;
        public const ulong BlockHash = 20;
        public const ulong SelfDestruct = 0;
        public const ulong SelfDestructEip150 = 5000;
        public const ulong ExtCodeHash = 400;
        public const ulong ExtCodeHashEip1884 = 700;
        public const ulong SelfBalance = 5;
        public const ulong InitCodeWord = 2; //eip-3860 gas per word cost for init code size

        public const ulong ColdSLoad = 2100; // eip-2929

        public const ulong ColdAccountAccess = 2600; // eip-2929
        public const ulong WarmStateRead = 100; // eip-2929
        public const ulong CallPrecompileEip2929 = 100; // eip-2929

        public const ulong AccessAccountListEntry = 2400; // eip-2930
        public const ulong AccessStorageListEntry = 1900; // eip-2930
        public const ulong TLoad = WarmStateRead; // eip-1153
        public const ulong TStore = WarmStateRead; // eip-1153
        public const ulong PerAuthBaseCost = 12500; // eip-7702
        public const ulong TotalCostFloorPerTokenEip7623 = 10; // eip-7632

        // EIP-8037: Two-dimensional gas metering constants.
        // Devnet-3 keeps CPSB hardcoded and replaces it with dynamic CPSB in devnet-4.
        public const ulong CostPerStateByte = 1174;
        public const ulong SSetRegular = 2_900;
        public const ulong SSetState = 32 * CostPerStateByte;
        public const ulong CreateRegular = 9_000;
        public const ulong CreateState = 112 * CostPerStateByte;
        public const ulong NewAccountState = 112 * CostPerStateByte;
        public const ulong CodeDepositRegularPerWord = 6;
        public const ulong CodeDepositState = CostPerStateByte;
        public const ulong PerAuthBaseRegular = 7_500;
        public const ulong PerAuthBaseState = 23 * CostPerStateByte;
        public const ulong PerEmptyAccountState = 112 * CostPerStateByte;

        public const ulong TxDataNonZeroMultiplier = TxDataNonZero / TxDataZero;
        public const ulong TxDataNonZeroMultiplierEip2028 = TxDataNonZeroEip2028 / TxDataZero;

        public const ulong MinModExpEip2565 = 200; // eip-2565
        public const ulong MinModExpEip7883 = 500; // eip-7883

        // Eof Execution EIP-7692
        public const ulong DataLoad = 4;
        public const ulong DataLoadN = 3;
        public const ulong DataCopy = 3;
        public const ulong DataSize = 2;
        public const ulong ReturnCode = 0;
        public const ulong EofCreate = 32000;
        public const ulong ReturnDataLoad = 3;
        public const ulong RJump = 2;
        public const ulong RJumpi = 4;
        public const ulong RJumpv = 4;
        public const ulong Exchange = 3;
        public const ulong Swapn = 3;
        public const ulong Dupn = 3;
        public const ulong Callf = 5;
        public const ulong Jumpf = 5;
        public const ulong Retf = 3;
    }
}
