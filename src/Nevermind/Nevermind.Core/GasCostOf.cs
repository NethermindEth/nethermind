using System.Numerics;

namespace Nevermind.Core
{
    // cost provider (depending on the block number)
    public static class GasCostOf
    {
        public static long Zero = 0;
        public static long Base = 2;
        public static long VeryLow = 3;
        public static long Low = 5;
        public static long Mid = 8;
        public static long High = 10;
        public static long ExtCode = 20; // 700
        public static long ExtCodeSize = 20; // 700
        public static long Balance = 20; // 400
        public static long SLoad = 50; // 200
        public static long JumpDest = 1;
        public static long SSet = 20000;
        public static long SReset = 5000;
        public static long Destroy = 5000;
        public static long Create = 32000;
        public static long CodeDeposit = 200;
        public static long Call = 40; // 700
        public static long CallCode = 40; // 700
        public static long DelegateCall = 40; // 700
        public static long CallValue = 9000;
        public static long CallStipend = 2300;
        public static long NewAccount = 25000;
        public static long Exp = 10;
        public static long ExpByte = 10;
        public static long Memory = 3;
        public static long TxCreate = 32000;
        public static long TxDataZero = 4;
        public static long TxDataNonZero = 64;
        public static long Transaction = 21000;
        public static long Log = 375;
        public static long LogTopic = 375; // ?
        public static long LogData = 8;
        public static long Sha3 = 30;
        public static long Sha3Word = 6;
        public static long Copy = 3;
        public static long BlockHash = 20;
        public static long Suicide = 0; // 5000
    }
}