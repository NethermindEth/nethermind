using Nevermind.Core;
using Nevermind.Core.Potocol;

namespace Nevermind.Evm
{
    public class IntrinsicGasCalculator
    {
        public long Calculate(IEthereumRelease ethereumRelease, Transaction transaction)
        {
            long result = GasCostOf.Transaction;

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

            if (transaction.IsContractCreation && ethereumRelease.IsEip2Enabled)
            {
                result += GasCostOf.TxCreate;
            }

            return result;
        }
    }
}