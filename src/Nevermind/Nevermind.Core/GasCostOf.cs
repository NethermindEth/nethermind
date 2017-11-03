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
        public const ulong ExtCode = 20; // 700 // EIP-150
        public const ulong ExtCodeSize = 20; // 700 // EIP-150
        public const ulong Balance = 20; // 400 // EIP-150
        public const ulong SLoad = 50; // 200 // EIP-150
        public const ulong JumpDest = 1;
        public const ulong SSet = 20000;
        public const ulong SReset = 5000;
        public const ulong Destroy = 5000;
        public const ulong Create = 32000; // EIP-2
        public const ulong CodeDeposit = 0; // 200 // ??? I guess this is outside of EVM
        public const ulong Call = 40; // 700 // EIP-150
        public const ulong CallCode = 40; // 700 // EIP-150
        public const ulong DelegateCall = 40; // 700 // EIP-150
        public const ulong CallValue = 9000;
        public const ulong CallStipend = 2300;
        public const ulong NewAccount = 25000;
        public const ulong Exp = 10;
        public const ulong ExpByte = 10; // 50 // EIP-160
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
        public const ulong SelfDestruct = 0; // 5000  // EIP-150
    }
}