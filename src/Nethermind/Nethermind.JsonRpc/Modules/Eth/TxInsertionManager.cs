using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class TxInsertionManager
    {
        private GasPriceOracle _gasPriceOracle;
        private UInt256? _ignoreUnder;
        private bool _isEip1559Enabled;
        private UInt256 _baseFee;
        public TxInsertionManager(GasPriceOracle gasPriceOracle, UInt256? ignoreUnder, bool isEip1559Enabled,
            UInt256 baseFee)
        {
            _gasPriceOracle = gasPriceOracle;
            _ignoreUnder = ignoreUnder;
            _isEip1559Enabled = isEip1559Enabled;
            _baseFee = baseFee;
        }

        public int AddValidTxAndReturnCount(Block block)
        {
            if (TransactionsExistIn(block))
            {
                Transaction[] transactionsInBlock = block.Transactions;
                int countTxAdded = AddTxAndReturnCountAdded(transactionsInBlock, block);

                if (countTxAdded == 0)
                {
                    AddDefaultPriceToSortedTxList();
                    countTxAdded++;
                }

                return countTxAdded;
            }
            else
            {
                AddDefaultPriceToSortedTxList();
                return 1;
            }
        }

        private static bool TransactionsExistIn(Block block)
        {
            Transaction[] transactions = block.Transactions;
            return transactions.Length > 0;
        }

        private int AddTxAndReturnCountAdded(Transaction[] txInBlock, Block block)
        {
            int countTxAdded = 0;
            
            IEnumerable<Transaction> txsSortedByEffectiveGasPrice = txInBlock.OrderBy(EffectiveGasPrice);
            foreach (Transaction tx in txsSortedByEffectiveGasPrice)
            {
                if (TransactionCanBeAdded(tx, block)) //how should i set to be null?
                {
                    _gasPriceOracle.TxGasPriceList.Add(EffectiveGasPrice(tx));
                    countTxAdded++;
                }

                if (countTxAdded >= GasPriceConfig.TxLimitFromABlock)
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
            bool res = IsAboveMinPrice(transaction) && Eip1559ModeCompatible(transaction);
            return res && TxNotSentByBeneficiary(transaction, block);
        }

        private bool IsAboveMinPrice(Transaction transaction)
        {
            return transaction.GasPrice >= _ignoreUnder;
        }

        private bool Eip1559ModeCompatible(Transaction transaction)
        {
            if (_isEip1559Enabled == false)
            {
                return TransactionIsNotEip1559(transaction);
            }
            else
            {
                return true;
            }
        }

        private static bool TransactionIsNotEip1559(Transaction transaction)
        {
            return !transaction.IsEip1559;
        }

        private bool TxNotSentByBeneficiary(Transaction transaction, Block block)
        {
            if (block.Beneficiary == null)
            {
                return true;
            }

            return block.Beneficiary != transaction.SenderAddress;
        }

        private void AddDefaultPriceToSortedTxList()
        {
            _gasPriceOracle.TxGasPriceList.Add((UInt256)_gasPriceOracle.DefaultGasPrice!);
        }
    }
}
