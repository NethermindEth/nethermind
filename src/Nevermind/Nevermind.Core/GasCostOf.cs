using System.Numerics;

namespace Nevermind.Core
{
    // cost provider (depending on the block number)
    public static class GasCostOf
    {
        public static readonly BigInteger Zero = 0;
        public static readonly BigInteger Base = 2;
        public static readonly BigInteger VeryLow = 3;
        public static readonly BigInteger Low = 5;
        public static readonly BigInteger Mid = 8;
        public static readonly BigInteger High = 10;
        public static readonly BigInteger ExtCode = 20; // 700 // EIP-150
        public static readonly BigInteger ExtCodeSize = 20; // 700 // EIP-150
        public static readonly BigInteger Balance = 20; // 400 // EIP-150
        public static readonly BigInteger SLoad = 50; // 200 // EIP-150
        public static readonly BigInteger JumpDest = 1;
        public static readonly BigInteger SSet = 20000;
        public static readonly BigInteger SReset = 5000;
        public static readonly BigInteger Destroy = 5000;
        public static readonly BigInteger Create = 32000; // EIP-2
        public static readonly BigInteger CodeDeposit = 0; // 200 // ??? I guess this is outside of EVM
        public static readonly BigInteger Call = 40; // 700 // EIP-150
        public static readonly BigInteger CallCode = 40; // 700 // EIP-150
        public static readonly BigInteger DelegateCall = 40; // 700 // EIP-150
        public static readonly BigInteger CallValue = 9000;
        public static readonly BigInteger CallStipend = 2300;
        public static readonly BigInteger NewAccount = 25000;
        public static readonly BigInteger Exp = 10;
        public static readonly BigInteger ExpByte = 10; // 50 // EIP-160
        public static readonly BigInteger Memory = 3;
        public static readonly BigInteger TxCreate = 32000;
        public static readonly BigInteger TxDataZero = 4;
        public static readonly BigInteger TxDataNonZero = 64;
        public static readonly BigInteger Transaction = 21000;
        public static readonly BigInteger Log = 375;
        public static readonly BigInteger LogTopic = 375; // ?
        public static readonly BigInteger LogData = 8;
        public static readonly BigInteger Sha3 = 30;
        public static readonly BigInteger Sha3Word = 6;
        public static readonly BigInteger Copy = 3;
        public static readonly BigInteger BlockHash = 20;
        public static readonly BigInteger SelfDestruct = 0; // 5000  // EIP-150
    }
}