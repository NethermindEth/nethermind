//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class GasPriceEstimateTxInsertionManager : ITxInsertionManager
    {
        private readonly IGasPriceOracle _gasPriceOracle;
        private readonly UInt256? _ignoreUnder;
        private readonly ISpecProvider _specProvider;
        public GasPriceEstimateTxInsertionManager(IGasPriceOracle gasPriceOracle, UInt256? ignoreUnder, 
            ISpecProvider specProvider)
        {
            _gasPriceOracle = gasPriceOracle;
            _ignoreUnder = ignoreUnder;
            _specProvider = specProvider;
        }

        public int AddValidTxFromBlockAndReturnCount(Block block)
        {
            List<UInt256> txGasPriceList = GetTxGasPriceList(_gasPriceOracle);
            if (block.Transactions.Length > 0)
            {
                Transaction[] transactionsInBlock = block.Transactions;
                int countTxAdded = AddTxAndReturnCountAdded(transactionsInBlock, block);

                if (countTxAdded == 0)
                {
                    txGasPriceList.Add((UInt256) _gasPriceOracle.FallbackGasPrice!);
                    countTxAdded++;
                }

                return countTxAdded;
            }
            else
            {
                txGasPriceList.Add((UInt256) _gasPriceOracle.FallbackGasPrice!);
                return 1;
            }
        }

        private int AddTxAndReturnCountAdded(Transaction[] txInBlock, Block block)
        {
            int countTxAdded = 0;
            bool eip1559Enabled = _specProvider.GetSpec(block.Number).IsEip1559Enabled;
            UInt256 baseFee = block.BaseFeePerGas;
            IEnumerable<Transaction> txsSortedByEffectiveGasPrice = txInBlock.OrderBy(tx => EffectiveGasPrice(tx, eip1559Enabled, baseFee));
            foreach (Transaction tx in txsSortedByEffectiveGasPrice)
            {
                if (TransactionCanBeAdded(tx, block, eip1559Enabled))
                {
                    List<UInt256> txGasPriceList = GetTxGasPriceList(_gasPriceOracle);
                    txGasPriceList.Add(EffectiveGasPrice(tx, eip1559Enabled, baseFee));
                    countTxAdded++;
                }

                if (countTxAdded >= EthGasPriceConstants.TxLimitFromABlock)
                {
                    break;
                }
            }

            return countTxAdded;
        }


        private UInt256 EffectiveGasPrice(Transaction transaction, bool eip1559Enabled, UInt256 baseFee)
        {
            return transaction.CalculateEffectiveGasPrice(eip1559Enabled, baseFee);
        }

        private bool TransactionCanBeAdded(Transaction transaction, Block block, bool eip1559Enabled)
        {
            return transaction.GasPrice >= _ignoreUnder && Eip1559ModeCompatible(transaction, eip1559Enabled) &&
                   TxNotSentByBeneficiary(transaction, block);
        }

        private bool Eip1559ModeCompatible(Transaction transaction, bool eip1559Enabled)
        {
            return eip1559Enabled || !transaction.IsEip1559;
        }

        private bool TxNotSentByBeneficiary(Transaction transaction, Block block)
        {
            if (block.Beneficiary == null)
            {
                return true;
            }

            return block.Beneficiary != transaction.SenderAddress;
        }

        protected internal virtual List<UInt256> GetTxGasPriceList(IGasPriceOracle gasPriceOracle)
        {
            return gasPriceOracle.TxGasPriceList;
        }
    }
}
