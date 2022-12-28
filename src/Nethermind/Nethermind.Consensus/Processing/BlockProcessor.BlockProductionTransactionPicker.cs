// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
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

            public event EventHandler<AddingTxEventArgs>? AddingTransaction;

            public AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx, IReadOnlySet<Transaction> transactionsInBlock, IWorldState worldState)
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

                IReleaseSpec spec = _specProvider.GetSpec(block.Header);

                if (currentTx.IsAboveInitCode(spec))
                {
                    return args.Set(TxAction.Skip, $"EIP-3860 - transaction size over max init code size");
                }

                if (worldState.IsInvalidContractSender(spec, currentTx.SenderAddress))
                {
                    return args.Set(TxAction.Skip, $"Sender is contract");
                }

                UInt256 expectedNonce = worldState.GetNonce(currentTx.SenderAddress);
                if (expectedNonce != currentTx.Nonce)
                {
                    return args.Set(TxAction.Skip, $"Invalid nonce - expected {expectedNonce}");
                }

                UInt256 balance = worldState.GetBalance(currentTx.SenderAddress);
                if (!HasEnoughFounds(currentTx, balance, args, block, spec))
                {
                    return args;
                }

                AddingTransaction?.Invoke(this, args);
                return args;
            }

            private bool HasEnoughFounds(Transaction transaction, in UInt256 senderBalance, AddingTxEventArgs e, Block block, IReleaseSpec releaseSpec)
            {
                bool eip1559Enabled = releaseSpec.IsEip1559Enabled;
                UInt256 transactionPotentialCost = transaction.CalculateTransactionPotentialCost(eip1559Enabled, block.BaseFeePerGas);

                if (senderBalance < transactionPotentialCost)
                {
                    e.Set(TxAction.Skip, $"Transaction cost ({transactionPotentialCost}) is higher than sender balance ({senderBalance})");
                    return false;
                }

                if (eip1559Enabled && !transaction.IsServiceTransaction && senderBalance < (UInt256)transaction.GasLimit * transaction.MaxFeePerGas + transaction.Value)
                {
                    e.Set(TxAction.Skip, $"MaxFeePerGas ({transaction.MaxFeePerGas}) times GasLimit {transaction.GasLimit} is higher than sender balance ({senderBalance})");
                    return false;
                }

                return true;
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
