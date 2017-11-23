using Nevermind.Core;
using Nevermind.Core.Potocol;

namespace Nevermind.Evm
{
    public class IntrinsicGasCalculator
    {
        public long Calculate(IProtocolSpecification protocolSpecification, Transaction transaction)
        {
            long result = GasCostOf.Transaction;

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

            if (transaction.IsContractCreation && protocolSpecification.IsEip2Enabled)
            {
                result += GasCostOf.TxCreate;
            }

            return result;
        }
    }
}