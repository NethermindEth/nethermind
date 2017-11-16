namespace Nevermind.Core
{
    // cost provider (depending on the block number)
    public static class GasCostOf
    {
        public const ulong Zero = 0;
        public const ulong Base = 2;
        public const ulong VeryLow = 3;
        public const ulong Low = 5;
        public const ulong Mid = 8;
        public const ulong High = 10;
        public const ulong ExtCode = 20;
        public const ulong ExtCodeEip150 = 700;
        public const ulong ExtCodeSize = 20;
        public const ulong ExtCodeSizeEip150 = 700;
        public const ulong Balance = 20;
        public const ulong BalanceEip150 = 400;
        public const ulong SLoad = 50;
        public const ulong SLoadEip150 = 200;
        public const ulong JumpDest = 1;
        public const ulong SSet = 20000;
        public const ulong SReset = 5000;
        public const ulong Destroy = 5000;
        public const ulong Create = 32000;
        public const ulong CodeDeposit = 200;
        public const ulong CallOrCallCode = 40;
        public const ulong CallOrCallCodeEip150 = 700;
        public const ulong DelegateCall = 40;
        public const ulong DelegateCallEip150 = 700;
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
        public const ulong Transaction = 21000;
        public const ulong Log = 375;
        public const ulong LogTopic = 375; // ?
        public const ulong LogData = 8;
        public const ulong Sha3 = 30;
        public const ulong Sha3Word = 6;
        public const ulong Copy = 3;
        public const ulong BlockHash = 20;
        public const ulong SelfDestruct = 0;
        public const ulong SelfDestructEip150 = 5000;
    }
}