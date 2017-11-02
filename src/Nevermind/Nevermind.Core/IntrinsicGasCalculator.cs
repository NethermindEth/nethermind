using System.Numerics;

namespace Nevermind.Core
{
    public class IntrinsicGasCalculator
    {
        public BigInteger HomesteadBlockNumber = 1150000;

        public ulong Calculate(Transaction transaction, BigInteger blockNumber)
        {
            ulong result = GasCostOf.Transaction;

            // compare perf for BigInteger operations and ints
            if (transaction.Data != null)
            {
                for (int i = 0; i < transaction.Data.Length; i++)
                {
                    result += transaction.Data[i] == 0 ? GasCostOf.TxDataZero : GasCostOf.TxDataNonZero;
                }
            }
            else if (transaction.Init != null)
            {
                for (int i = 0; i < transaction.Init.Length; i++)
                {
                    result += transaction.Init[i] == 0 ? GasCostOf.TxDataZero : GasCostOf.TxDataNonZero;
                }
            }

            if (transaction.IsContractCreation && blockNumber >= HomesteadBlockNumber)
            {
                result += GasCostOf.TxCreate;
            }

            return result;
        }
    }
}