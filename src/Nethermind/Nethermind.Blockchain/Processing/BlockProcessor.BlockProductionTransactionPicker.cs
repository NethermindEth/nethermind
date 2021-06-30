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

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        protected class BlockProductionTransactionPicker
        {
            private readonly ISpecProvider _specProvider;
            
            public BlockProductionTransactionPicker(ISpecProvider specProvider)
            {
                _specProvider = specProvider;
            }
            
            public event EventHandler<TxCheckEventArgs>? CheckTransaction;

            public TxCheckEventArgs CanAddTransaction(Block block, Transaction currentTx, IReadOnlySet<Transaction> transactionsInBlock, IStateProvider stateProvider)
            {
                bool HasEnoughFounds(Transaction transaction, UInt256 senderBalance, TxCheckEventArgs e)
                {
                    IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Number);
                    bool eip1559Enabled = releaseSpec.IsEip1559Enabled; 
                    UInt256 transactionPotentialCost = transaction.CalculateTransactionPotentialCost(eip1559Enabled, block.BaseFeePerGas);

                    if (senderBalance < transactionPotentialCost)
                    {
                        e.Set(TxAction.Skip, $"Transaction cost ({transactionPotentialCost}) is higher than sender balance ({senderBalance})");
                        return false;
                    }

                    if (transaction.IsEip1559 && !transaction.IsServiceTransaction && senderBalance < (UInt256)transaction.GasLimit * transaction.MaxFeePerGas)
                    {
                        e.Set(TxAction.Skip, $"MaxFeePerGas({transaction.MaxFeePerGas}) times GasLimit {transaction.GasLimit} is higher than sender balance ({senderBalance})");
                        return false;
                    }
                    
                    return true;
                }
                
                TxCheckEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);
                
                // No more gas available in block
                long gasRemaining = block.Header.GasLimit - block.GasUsed;
                if (GasCostOf.Transaction > gasRemaining)
                {
                    return args.Set(TxAction.Stop, "Block full");
                }
                
                if (currentTx.SenderAddress is null)
                {
                    return args.Set(TxAction.Skip, "Null sender");
                }

                if (transactionsInBlock.Contains(currentTx))
                {
                    return args.Set(TxAction.Skip, "Transaction already in block");
                }

                // No gas available in block for tx
                if (currentTx.GasLimit > gasRemaining)
                {
                    return args.Set(TxAction.Skip, $"Not enough gas in block, gas limit {currentTx.GasLimit} > {gasRemaining}");
                }

                UInt256 expectedNonce = stateProvider.GetNonce(currentTx.SenderAddress);
                if (expectedNonce != currentTx.Nonce)
                {
                    return args.Set(TxAction.Skip, $"Invalid nonce - expected {expectedNonce}");
                }

                UInt256 balance = stateProvider.GetBalance(currentTx.SenderAddress);
                if (!HasEnoughFounds(currentTx, balance, args))
                {
                    return args;
                }

                CheckTransaction?.Invoke(this, args);
                return args;
            }
        }
        
        public enum TxAction
        {
            Add,
            Skip,
            Stop
        }
    }
}
