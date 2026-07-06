// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
        public const ulong PerAuthBaseCost = Eip7702Constants.PerAuthBaseCost;
        public const ulong TotalCostFloorPerTokenEip7623 = 10; // eip-7623
        public const ulong TotalCostFloorPerTokenEip7976 = 16; // eip-7976

        public const ulong CostPerStateByte = 1530; // eip-8037
        public const ulong StateBytesPerStorageSet = 64; // eip-8037
        public const ulong StateBytesPerNewAccount = 120; // eip-8037
        public const ulong StateBytesPerAuthBase = Eip8037Constants.StateBytesPerAuthBase;
        public const ulong SSetRegular = 2_900;
        public const ulong SSetState = StateBytesPerStorageSet * CostPerStateByte;
        public const ulong CreateRegular = 9_000;
        public const ulong CreateState = StateBytesPerNewAccount * CostPerStateByte;
        public const ulong NewAccountState = StateBytesPerNewAccount * CostPerStateByte;
        public const ulong CodeDepositRegularPerWord = 6;
        public const ulong CodeDepositState = CostPerStateByte;
        public const ulong PerAuthBaseRegular = Eip8037Constants.PerAuthBaseRegularCost;
        public const ulong PerAuthBaseState = StateBytesPerAuthBase * CostPerStateByte;
        public const ulong PerEmptyAccountState = StateBytesPerNewAccount * CostPerStateByte;
        public const ulong BlockAccessListItem = Eip7928Constants.ItemCost; // eip-7928

        public const ulong TxDataNonZeroMultiplier = TxDataNonZero / TxDataZero;
        public const ulong TxDataNonZeroMultiplierEip2028 = TxDataNonZeroEip2028 / TxDataZero;

        public const ulong MinModExpEip2565 = 200; // eip-2565
        public const ulong MinModExpEip7883 = 500; // eip-7883

        // eip-2780: reduce intrinsic transaction gas and reprice state-touching primitives.
        public const ulong TransactionEip2780 = 12000; // TX_BASE_COST: ECDSA recovery + sender account access + sender account write
        public const ulong TxValueCostEip2780 = 4244; // recipient balance write for a value-bearing transfer (non-create)
        public const ulong TransferLogEip2780 = 1756; // eip-7708 LOG3 transfer event: 375 + 3*375 + 32*8
    }
}
