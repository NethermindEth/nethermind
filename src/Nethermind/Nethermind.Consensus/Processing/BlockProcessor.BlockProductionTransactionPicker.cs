// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionPicker : IBlockProductionTransactionPicker
        {
            private readonly long _maxTxLengthBytes;

            protected readonly ISpecProvider _specProvider;
            private readonly bool _ignoreEip3607;

            public BlockProductionTransactionPicker(ISpecProvider specProvider, long maxTxLengthKilobytes = BlocksConfig.DefaultMaxTxKilobytes, bool ignoreEip3607 = false)
            {
                _specProvider = specProvider;
                _maxTxLengthBytes = maxTxLengthKilobytes.KiB();
                _ignoreEip3607 = ignoreEip3607;
            }

            public event EventHandler<AddingTxEventArgs>? AddingTransaction;

            protected void OnAddingTransaction(AddingTxEventArgs e)
            {
                AddingTransaction?.Invoke(this, e);
            }

            public virtual AddingTxEventArgs CanAddTransaction(Block block, Transaction currentTx, IReadOnlySet<Transaction> transactionsInBlock, IWorldState stateProvider)
            {
                AddingTxEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);

                long gasRemaining = block.Header.GasLimit - block.GasUsed;

                // No more gas available in block for any transactions,
                // the only case we have to really stop
                if (GasCostOf.Transaction > gasRemaining)
                {
                    return args.Set(TxAction.Stop, "Block full");
                }

                if (block is BlockToProduce blockToProduce && blockToProduce.TxByteLength + currentTx.GetLength() > _maxTxLengthBytes)
                {
                    return args.Set(
                        // If smallest tx is too large, stop picking
                        currentTx.GasLimit == GasCostOf.Transaction ? TxAction.Stop : TxAction.Skip,
                        "Too large for CL");
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
                    return args.Set(TxAction.Skip, TransactionResult.TransactionSizeOverMaxInitCodeSize.Error);
                }

                if (!_ignoreEip3607 && stateProvider.IsInvalidContractSender(spec, currentTx.SenderAddress))
                {
                    return args.Set(TxAction.Skip, $"Sender is contract");
                }

                UInt256 expectedNonce = stateProvider.GetNonce(currentTx.SenderAddress);
                if (expectedNonce != currentTx.Nonce)
                {
                    return args.Set(TxAction.Skip, $"Invalid nonce - expected {expectedNonce}");
                }

                UInt256 balance = stateProvider.GetBalance(currentTx.SenderAddress);
                if (!HasEnoughFunds(currentTx, balance, args, block, spec))
                {
                    return args;
                }

                OnAddingTransaction(args);
                return args;
            }

            private static bool HasEnoughFunds(Transaction transaction, in UInt256 senderBalance, AddingTxEventArgs e, Block block, IReleaseSpec releaseSpec)
            {
                bool eip1559Enabled = releaseSpec.IsEip1559Enabled;
                UInt256 transactionPotentialCost = transaction.CalculateTransactionPotentialCost(eip1559Enabled, block.BaseFeePerGas);

                if (senderBalance < transactionPotentialCost)
                {
                    e.Set(TxAction.Skip, $"Transaction cost ({transactionPotentialCost}) is higher than sender balance ({senderBalance})");
                    return false;
                }

                if (!transaction.IsServiceTransaction && eip1559Enabled)
                {
                    UInt256 maxFee = (UInt256)transaction.GasLimit * transaction.MaxFeePerGas + transaction.Value;

                    if (senderBalance < maxFee)
                    {
                        e.Set(TxAction.Skip, $"{maxFee} is higher than sender balance ({senderBalance}), MaxFeePerGas: ({transaction.MaxFeePerGas}), GasLimit {transaction.GasLimit}");
                        return false;
                    }

                    if (transaction.SupportsBlobs && (
                        !BlobGasCalculator.TryCalculateBlobBaseFee(block.Header, transaction, releaseSpec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee) ||
                        senderBalance < (maxFee += blobBaseFee)))
                    {
                        e.Set(TxAction.Skip, $"{maxFee} is higher than sender balance ({senderBalance}), MaxFeePerGas: ({transaction.MaxFeePerGas}), GasLimit {transaction.GasLimit}, BlobBaseFee: {blobBaseFee}");
                        return false;
                    }
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
