using System.Numerics;

namespace Nevermind.Core
{
    public static class GasCostOf
    {
        public static BigInteger Zero = 0;
        public static BigInteger Base = 2;
        public static BigInteger VeryLow = 3;
        public static BigInteger Low = 5;
        public static BigInteger Mid = 8;
        public static BigInteger High = 10;
        //public static BigInteger ExtCode = 700;
        public static BigInteger ExtCode = 20;
        //public static BigInteger Balance = 400;
        public static BigInteger Balance = 20;
        //public static BigInteger SLoad = 200;
        public static BigInteger SLoad = 50;
        public static BigInteger JumpDest = 1;
        public static BigInteger SSet = 20000;
        public static BigInteger SReset = 5000;
        public static BigInteger Destroy = 5000;
        public static BigInteger Create = 32000;
        public static BigInteger CodeDeposit = 200;
        public static BigInteger Call = 700;
        public static BigInteger CallValue = 9000;
        public static BigInteger CallStipend = 2300;
        public static BigInteger NewAccount = 25000;
        public static BigInteger Exp = 10;
        public static BigInteger ExpByte = 10;
        public static BigInteger Memory = 3;
        public static BigInteger TxCreate = 32000;
        public static BigInteger TxDataZero = 4;
        public static BigInteger TxDataNonZero = 64;
        public static BigInteger Transaction = 21000;
        public static BigInteger Log = 375;
        public static BigInteger LogData = 8;
        public static BigInteger Sha3 = 30;
        public static BigInteger Sha3Word = 6;
        public static BigInteger Copy = 3;
        public static BigInteger BlockHash = 20;

    }
}