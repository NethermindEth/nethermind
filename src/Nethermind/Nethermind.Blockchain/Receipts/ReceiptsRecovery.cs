// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public class ReceiptsRecovery(IEthereumEcdsa? ecdsa, ISpecProvider? specProvider, bool reinsertReceiptOnRecover = true) : IReceiptsRecovery
    {
        private readonly IEthereumEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly bool _reinsertReceiptOnRecover = reinsertReceiptOnRecover;

        public ReceiptsRecoveryResult TryRecover(ReceiptRecoveryBlock block, TxReceipt[] receipts, bool forceRecoverSender = true)
        {
            bool canRecover = block.TransactionCount == receipts?.Length;
            if (canRecover)
            {
                bool needRecover = NeedRecover(receipts, forceRecoverSender);
                if (needRecover)
                {
                    using IReceiptsRecovery.IRecoveryContext ctx = CreateRecoveryContext(block, forceRecoverSender);
                    for (int receiptIndex = 0; receiptIndex < receipts.Length; receiptIndex++)
                    {
                        TxReceipt receipt = receipts[receiptIndex];
                        ctx.RecoverReceiptData(receipt);
                    }

                    if (_reinsertReceiptOnRecover)
                    {
                        return ReceiptsRecoveryResult.NeedReinsert;
                    }

                    return ReceiptsRecoveryResult.Success;
                }

                return ReceiptsRecoveryResult.Skipped;
            }

            return ReceiptsRecoveryResult.Fail;
        }

        public IReceiptsRecovery.IRecoveryContext CreateRecoveryContext(ReceiptRecoveryBlock block, bool forceRecoverSender = false)
        {
            IReleaseSpec releaseSpec = _specProvider.GetSpec(block.Header);
            return new RecoveryContext(releaseSpec, block, forceRecoverSender, _ecdsa);
        }

        public bool NeedRecover(TxReceipt[] receipts, bool forceRecoverSender = true, bool recoverSenderOnly = false)
        {
            if (receipts is null || receipts.Length == 0 || (recoverSenderOnly && !forceRecoverSender)) return false;

            for (int i = 0; i < receipts.Length; i++)
            {
                TxReceipt receipt = receipts[i];
                if (recoverSenderOnly)
                {
                    if (receipt.Sender is null) return true;
                }
                else if (receipt.BlockHash is null ||
                         receipt.TxHash is null ||
                         (forceRecoverSender && receipt.Sender is null))
                {
                    return true;
                }
            }

            return false;
        }

        private class RecoveryContext(IReleaseSpec releaseSpec, ReceiptRecoveryBlock block, bool forceRecoverSender, IEthereumEcdsa ecdsa) : IReceiptsRecovery.IRecoveryContext
        {
            private readonly IReleaseSpec _releaseSpec = releaseSpec;
            private ReceiptRecoveryBlock _block = block;
            private readonly bool _forceRecoverSender = forceRecoverSender;
            private readonly IEthereumEcdsa _ecdsa = ecdsa;

            private ulong _gasUsedBefore = 0;
            private int _transactionIndex = 0;

            public void RecoverReceiptData(TxReceipt receipt)
            {
                if (_transactionIndex >= _block.TransactionCount)
                {
                    throw new InvalidOperationException("Trying to recover more receipt that transaction");
                }

                Transaction transaction = _block.GetNextTransaction();

                if (transaction.SenderAddress is null && _forceRecoverSender)
                {
                    transaction.SenderAddress = _ecdsa.RecoverAddress(transaction, !_releaseSpec.ValidateChainId);
                }

                receipt.TxType = transaction.Type;
                receipt.BlockHash = _block.Hash;
                receipt.BlockNumber = _block.Number;
                receipt.TxHash = transaction.Hash;
                receipt.Index = _transactionIndex;
                receipt.Sender ??= transaction.SenderAddress;
                receipt.Recipient = transaction.IsContractCreation ? null : transaction.To;

                // how would it be in CREATE2?
                receipt.ContractAddress = transaction.CreatesTopLevelContract && transaction.SenderAddress is not null ? ContractAddress.From(receipt.Sender, transaction.Nonce) : null;
                receipt.GasUsed = receipt.GasUsedTotal - _gasUsedBefore;
                // The log-count heuristic below assumes a failed transaction has no logs; a frame transaction
                // can fail while carrying the logs of the frames that succeeded (EIP-8141).
                if (receipt.StatusCode != StatusCode.Success && receipt.TxType != TxType.FrameTx)
                {
                    receipt.StatusCode = (receipt.Logs?.Length ?? 0) == 0 ? StatusCode.Failure : StatusCode.Success;
                }

                IncrementContext(receipt.GasUsedTotal);
            }

            public void RecoverReceiptData(ref TxReceiptStructRef receipt)
            {
                if (_transactionIndex >= _block.TransactionCount)
                {
                    throw new InvalidOperationException("Trying to recover more receipt that transaction");
                }

                Transaction transaction = _block.GetNextTransaction();

                receipt.TxType = transaction.Type;
                receipt.BlockHash = _block.Hash!.ToStructRef();
                receipt.BlockNumber = _block.Number;
                receipt.TxHash = transaction.Hash!.ToStructRef();
                receipt.Index = _transactionIndex;
                if (receipt.Sender.Bytes == Address.Zero.Bytes)
                {
                    receipt.Sender = (transaction.SenderAddress ?? (_forceRecoverSender ? _ecdsa.RecoverAddress(transaction, !_releaseSpec.ValidateChainId) : Address.Zero))!.ToStructRef();
                }
                receipt.Recipient = (transaction.IsContractCreation ? Address.Zero : transaction.To)!.ToStructRef();

                // how would it be in CREATE2?
                receipt.ContractAddress = (transaction.CreatesTopLevelContract && transaction.SenderAddress is not null ? ContractAddress.From(receipt.Sender.ToAddress(), transaction.Nonce) : Address.Zero)!.ToStructRef();
                receipt.GasUsed = receipt.GasUsedTotal - _gasUsedBefore;
                // See the note on the same heuristic in the overload above.
                if (receipt.StatusCode != StatusCode.Success && receipt.TxType != TxType.FrameTx)
                {
                    receipt.StatusCode = (receipt.Logs?.Length ?? 0) == 0 ? StatusCode.Failure : StatusCode.Success;
                }

                IncrementContext(receipt.GasUsedTotal);
            }

            private void IncrementContext(ulong gasUsedTotal)
            {
                _transactionIndex++;
                _gasUsedBefore = gasUsedTotal;
            }

            public void Dispose() => _block.Dispose();
        }
    }
}
