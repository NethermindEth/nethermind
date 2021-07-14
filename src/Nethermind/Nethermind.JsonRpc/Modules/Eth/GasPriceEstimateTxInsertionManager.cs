using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class GasPriceEstimateTxInsertionManager : ITxInsertionManager
    {
        private readonly IGasPriceOracle _gasPriceOracle;
        private readonly UInt256? _ignoreUnder;
        private readonly UInt256 _baseFee;
        private readonly bool _isEip1559Enabled;
        public GasPriceEstimateTxInsertionManager(IGasPriceOracle gasPriceOracle, UInt256? ignoreUnder, 
            UInt256 baseFee, bool isEip1559Enabled)
        {
            _gasPriceOracle = gasPriceOracle;
            _ignoreUnder = ignoreUnder;
            _isEip1559Enabled = isEip1559Enabled;
            _baseFee = baseFee;
        }

        public int AddValidTxFromBlockAndReturnCount(Block block)
        {
            if (block.Transactions.Length > 0)
            {
                Transaction[] transactionsInBlock = block.Transactions;
                int countTxAdded = AddTxAndReturnCountAdded(transactionsInBlock, block);

                if (countTxAdded == 0)
                {
                    GetTxGasPriceList().Add((UInt256) _gasPriceOracle.FallbackGasPrice!);
                    countTxAdded++;
                }

                return countTxAdded;
            }
            else
            {
                GetTxGasPriceList().Add((UInt256) _gasPriceOracle.FallbackGasPrice!);
                return 1;
            }
        }

        private int AddTxAndReturnCountAdded(Transaction[] txInBlock, Block block)
        {
            int countTxAdded = 0;
            
            IEnumerable<Transaction> txsSortedByEffectiveGasPrice = txInBlock.OrderBy(EffectiveGasPrice);
            foreach (Transaction tx in txsSortedByEffectiveGasPrice)
            {
                if (TransactionCanBeAdded(tx, block))
                {
                    GetTxGasPriceList().Add(EffectiveGasPrice(tx));
                    countTxAdded++;
                }

                if (countTxAdded >= EthGasPriceConstants.TxLimitFromABlock)
                {
                    break;
                }
            }

            return countTxAdded;
        }


        private UInt256 EffectiveGasPrice(Transaction transaction)
        {
            return transaction.CalculateEffectiveGasPrice(_isEip1559Enabled, _baseFee);
        }

        private bool TransactionCanBeAdded(Transaction transaction, Block block)
        {
            return transaction.GasPrice >= _ignoreUnder && Eip1559ModeCompatible(transaction) &&
                   TxNotSentByBeneficiary(transaction, block);
        }

        private bool Eip1559ModeCompatible(Transaction transaction)
        {
            return _isEip1559Enabled || !transaction.IsEip1559;
        }

        private bool TxNotSentByBeneficiary(Transaction transaction, Block block)
        {
            if (block.Beneficiary == null)
            {
                return true;
            }

            return block.Beneficiary != transaction.SenderAddress;
        }

        protected virtual List<UInt256> GetTxGasPriceList()
        {
            return _gasPriceOracle.TxGasPriceList;
        }
    }
}
