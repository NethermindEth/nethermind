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
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain.Processing
{
    public partial class BlockProcessor
    {
        /// <summary>
        /// Allows to pick which transactions can be added to a block during block production.
        /// </summary>
        protected internal class BlockProductionTransactionPicker
        {
            private readonly ISpecProvider _specProvider;
            
            public BlockProductionTransactionPicker(ISpecProvider specProvider)
            {
                _specProvider = specProvider;
            }
            
            /// <summary>
            /// Checks if a transaction can be added to a block.
            /// </summary>
            /// <param name="block">Block that is being constructed.</param>
            /// <param name="currentTx">Transaction that is being checked.</param>
            /// <param name="transactionsInBlock">Previously accumulated transactions in this block.</param>
            /// <param name="stateProvider">Access to current state of block that is under construction.</param>
            /// <returns>Detailed information about state of adding <see cref="currentTx"/> to <see cref="block"/>.</returns>
            public AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx, TransactionsInBlock transactionsInBlock, IStateProvider stateProvider)
            {
                AddingTxEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);
                
                long gasRemaining = block.Header.GasLimit - block.GasUsed;
                
                // No more gas available in block for any transactions,
                // the only case we have to really stop
                if (GasCostOf.Transaction > gasRemaining)
                {
                    return args.Set(TxAction.Stop, "Block full");
                }
                
                if (currentTx.SenderAddress is null)
                {
                    return args.Set(TxAction.Skip, "Null sender");
                }

                if (currentTx.GasLimit > gasRemaining)
                {
                    return args.Set(TxAction.Skip, $"Not enough gas in block, gas limit {currentTx.GasLimit} > {gasRemaining}");
                }
                
                if (transactionsInBlock.Contains(currentTx))
                {
                    return args.Set(TxAction.Skip, "Transaction already in block");
                }

                IReleaseSpec spec = _specProvider.GetSpec(block.Number);
                if (!transactionsInBlock.CanAddTx(spec.IsEip4488Enabled, currentTx))
                {
                    return args.Set(TxAction.Skip, "Block CallData limit exceeded");
                }

                if (stateProvider.IsInvalidContractSender(spec, currentTx.SenderAddress))
                {
                    return args.Set(TxAction.Skip, $"Sender is contract");
                }

                UInt256 expectedNonce = stateProvider.GetNonce(currentTx.SenderAddress);
                if (expectedNonce != currentTx.Nonce)
                {
                    return args.Set(TxAction.Skip, $"Invalid nonce - expected {expectedNonce}");
                }

                UInt256 balance = stateProvider.GetBalance(currentTx.SenderAddress);
                return CheckHasEnoughFounds(currentTx, balance, args, block, spec);
            }

            /// <summary>
            /// Checks if <see cref="transaction"/> has enough funds to be added to a block.
            /// </summary>
            /// <remarks>
            /// On POSDAO chains we support <see cref="Transaction.IsServiceTransaction"/> transactions.
            /// </remarks>
            private AddingTxEventArgs CheckHasEnoughFounds(Transaction transaction, in UInt256 senderBalance, AddingTxEventArgs e, Block block, IReleaseSpec releaseSpec)
            {
                bool eip1559Enabled = releaseSpec.IsEip1559Enabled;
                UInt256 transactionPotentialCost = transaction.CalculateTransactionPotentialCost(eip1559Enabled, block.BaseFeePerGas);

                if (senderBalance < transactionPotentialCost)
                {
                    e.Set(TxAction.Skip, $"Transaction cost ({transactionPotentialCost}) is higher than sender balance ({senderBalance})");
                }
                else if (eip1559Enabled && !transaction.IsServiceTransaction && senderBalance < (UInt256)transaction.GasLimit * transaction.MaxFeePerGas + transaction.Value)
                {
                    e.Set(TxAction.Skip, $"MaxFeePerGas ({transaction.MaxFeePerGas}) times GasLimit {transaction.GasLimit} is higher than sender balance ({senderBalance})");
                }

                return e;
            }
        }
        
        /// <summary>
        /// If transaction can be added and accumulated in constructed block.
        /// </summary>
        public enum TxAction
        {
            /// <summary>
            /// Transaction can be added, we can continue adding further transactions.
            /// </summary>
            Add,
            
            /// <summary>
            /// Transaction can't be added, we can continue adding further transactions.
            /// </summary>
            Skip,
            
            /// <summary>
            /// Transaction can't be added, we can't continue adding further transactions.
            /// </summary>
            Stop
        }
    }
}
