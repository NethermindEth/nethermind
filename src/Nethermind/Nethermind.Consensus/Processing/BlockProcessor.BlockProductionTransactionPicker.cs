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
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing
{
    public partial class BlockProcessor
    {
        public class BlockProductionTransactionPicker(
            ISpecProvider specProvider,
            long maxTxLengthKilobytes = BlocksConfig.DefaultMaxTxKilobytes,
            bool ignoreEip3607 = false)
            : IBlockProductionTransactionPicker
        {
            private readonly long _maxTxLengthBytes = maxTxLengthKilobytes.KiB;

            protected readonly ISpecProvider _specProvider = specProvider;

            public event EventHandler<AddingTxEventArgs>? AddingTransaction;

            protected void OnAddingTransaction(AddingTxEventArgs e) => AddingTransaction?.Invoke(this, e);

            public virtual AddingTxEventArgs CanAddTransaction(
                Block block,
                Transaction currentTx,
                IReadOnlySet<Transaction> transactionsInBlock,
                IReadOnlyStateProvider stateProvider,
                ulong cumulativeBlockExecutionGas,
                ulong cumulativeBlockStateGas)
            {
                AddingTxEventArgs args = new(transactionsInBlock.Count, currentTx, block, transactionsInBlock);

                ulong gasRemaining = block.Header.GasLimit.SaturatingSub(cumulativeBlockExecutionGas);
                IReleaseSpec spec = _specProvider.GetSpec(block.Header);

                // No more gas available in block for any transactions, the only case we have to really stop.
                // An EIP-8141 frame transaction reserves from its own intrinsic cost, below the legacy floor.
                ulong smallestTxGas = spec.IsEip8141Enabled ? (ulong)Eip8141Constants.IntrinsicGasCost : GasCostOf.Transaction;
                if (smallestTxGas > gasRemaining)
                {
                    return args.Set(TxAction.Stop, "Block full");
                }

                if (block is BlockToProduce blockToProduce && blockToProduce.TxByteLength + currentTx.GetLength(false) > _maxTxLengthBytes)
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

                if (transactionsInBlock.Contains(currentTx))
                {
                    return args.Set(TxAction.Skip, "Transaction already in block");
                }

                ulong stateGasRemaining = block.Header.GasLimit.SaturatingSub(cumulativeBlockStateGas);
                if (!Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(currentTx, spec, out ulong executionReservation, out ulong stateReservation))
                {
                    return args.Set(TxAction.Skip, "Cannot calculate frame transaction gas reservations");
                }

                if (executionReservation > gasRemaining)
                {
                    return args.Set(TxAction.Skip, $"Not enough execution gas in block, gas limit {executionReservation} > {gasRemaining}");
                }

                if (stateReservation > stateGasRemaining)
                {
                    return args.Set(TxAction.Skip, $"Not enough state gas in block, gas limit {stateReservation} > {stateGasRemaining}");
                }

                if (currentTx.IsAboveInitCode(spec))
                {
                    return args.Set(TxAction.Skip, TransactionResult.TransactionSizeOverMaxInitCodeSize.ErrorDescription);
                }

                // EIP-8141 exempts frame transactions from EIP-3607 ("Do not apply the restriction put
                // in place by EIP-3607 to frame transactions"), so the pool admits one from a contract
                // sender; without the same exemption here it could never be built into a block.
                if (!ignoreEip3607 && !currentTx.SupportsFrames && stateProvider.IsInvalidContractSender(spec, currentTx.SenderAddress))
                {
                    return args.Set(TxAction.Skip, $"Sender is contract");
                }

                // EIP-8250 moves a keyed transaction's replay protection to NONCE_MANAGER; both arms read the state
                // built up so far, so either also skips a candidate whose domain an earlier one in this block consumed.
                if (KeyedNonceManager.UsesKeyedNonce(currentTx))
                {
                    if (!KeyedNonceManager.IsNonceSetValid(stateProvider, currentTx.SenderAddress, currentTx.NonceKeys!, currentTx.Nonce))
                    {
                        return args.Set(TxAction.Skip, KeyedNonceSkipReason(stateProvider, currentTx));
                    }
                }
                else
                {
                    ulong expectedNonce = stateProvider.GetNonce(currentTx.SenderAddress);
                    if (expectedNonce != currentTx.Nonce)
                    {
                        return args.Set(TxAction.Skip, $"Invalid nonce - expected {expectedNonce}");
                    }
                }

                // A frame transaction's fees are paid by the frame that approves payment, which need not
                // be the sender, so a sender-balance gate here would skip transactions that do pay.
                if (!currentTx.SupportsFrames)
                {
                    UInt256 balance = stateProvider.GetBalance(currentTx.SenderAddress);
                    if (!HasEnoughFunds(currentTx, balance, args, block, spec))
                    {
                        return args;
                    }
                }

                OnAddingTransaction(args);
                return args;
            }

            /// <summary>Explains a keyed-nonce skip by naming the first key whose sequence disagrees with the candidate's.</summary>
            /// <remarks>Cold path only, reached once the candidate is already being skipped. Any single key of the set can be
            /// the one an earlier transaction in this block consumed, so a set-wide value would name a key that is current.</remarks>
            private static string KeyedNonceSkipReason(IReadOnlyStateProvider stateProvider, Transaction currentTx)
            {
                UInt256[] nonceKeys = currentTx.NonceKeys!;
                if (!KeyedNonceManager.AreNonceKeysWellFormed(nonceKeys))
                {
                    return "Invalid nonce sequence - malformed key set";
                }

                foreach (ref readonly UInt256 nonceKey in nonceKeys.AsSpan())
                {
                    ulong current = KeyedNonceManager.CurrentNonceSeq(stateProvider, currentTx.SenderAddress!, in nonceKey);
                    if (current != currentTx.Nonce)
                    {
                        return $"Invalid nonce sequence - key {nonceKey} expected {current}";
                    }
                }

                // Well-formed and every key at nonce_seq leaves exhaustion as the only reason the set was rejected.
                return $"Invalid nonce sequence - exhausted at {currentTx.Nonce}";
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

                    if (transaction.CarriesBlobs && (
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
