// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class GasCostOf
    {
        public const int Base = 2;
        public const int VeryLow = 3;
        public const int Low = 5;
        public const int Mid = 8;
        public const int High = 10;
        public const int ExtCode = 20;
        public const int ExtCodeEip150 = 700;
        public const int Balance = 20;
        public const int BalanceEip150 = 400;
        public const int BalanceEip1884 = 700;
        public const int SLoad = 50;
        public const int SLoadEip150 = 200;
        public const int SLoadEip1884 = 800;
        public const int JumpDest = 1;
        public const int SStoreNetMeteredEip1283 = 200;
        public const int SStoreNetMeteredEip2200 = 800;
        public const int SSet = 20000;
        public const int SReset = 5000;
        public const int Create = 32000;
        public const int CodeDeposit = 200;
        public const int Call = 40;
        public const int CallEip150 = 700;
        public const int CallValue = 9000;
        public const int CallStipend = 2300;
        public const int NewAccount = 25000;
        public const int Exp = 10;
        public const int ExpByte = 10;
        public const int ExpByteEip160 = 50;
        public const int Memory = 3;
        public const int TxCreate = 32000;
        public const int TxDataZero = 4;
        public const int TxDataNonZero = 68;
        public const int TxDataNonZeroEip2028 = 16;
        public const int Transaction = 21000;
        public const int BlobHash = 3;
        public const int Log = 375;
        public const int LogTopic = 375;
        public const int LogData = 8;
        public const int Sha3 = 30;
        public const int Sha3Word = 6;
        public const int BlockHash = 20;
        public const int SelfDestruct = 0;
        public const int SelfDestructEip150 = 5000;
        public const int ExtCodeHash = 400;
        public const int ExtCodeHashEip1884 = 700;
        public const int SelfBalance = 5;
        public const int InitCodeWord = 2; //eip-3860 gas per word cost for init code size

        public const int ColdSLoad = 2100; // eip-2929

        public const int ColdAccountAccess = 2600; // eip-2929
        public const int WarmStateRead = 100; // eip-2929

        public const int AccessAccountListEntry = 2400; // eip-2930
        public const int AccessStorageListEntry = 1900; // eip-2930
        public const int TLoad = WarmStateRead; // eip-1153
        public const int TStore = WarmStateRead; // eip-1153
    }
}
