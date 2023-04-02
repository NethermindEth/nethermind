// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Receipts
{
    public class ReceiptsRecovery : IReceiptsRecovery
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ISpecProvider _specProvider;
        private readonly bool _reinsertReceiptOnRecover;

        public ReceiptsRecovery(IEthereumEcdsa? ecdsa, ISpecProvider? specProvider, bool reinsertReceiptOnRecover = true)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _reinsertReceiptOnRecover = reinsertReceiptOnRecover;
        }

        public ReceiptsRecoveryResult TryRecover(Block block, TxReceipt[] receipts, bool forceRecoverSender = true)
        {
            var canRecover = block.Transactions.Length == receipts?.Length;
            if (canRecover)
            {
                var needRecover = NeedRecover(receipts, forceRecoverSender);
                if (needRecover)
                {
                    var ctx = CreateRecoveryContext(block, forceRecoverSender);
                    for (int receiptIndex = 0; receiptIndex < block.Transactions.Length; receiptIndex++)
                    {
                        if (receipts.Length > receiptIndex)
                        {
                            TxReceipt receipt = receipts[receiptIndex];
                            ctx.RecoverReceiptData(receipt);
                        }
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

        public IReceiptsRecovery.IRecoveryContext CreateRecoveryContext(Block block, bool forceRecoverSender = true)
        {
            var releaseSpec = _specProvider.GetSpec(block.Header);
            return new RecoveryContext(releaseSpec, block, forceRecoverSender, _ecdsa);
        }

        public bool NeedRecover(TxReceipt[] receipts, bool forceRecoverSender = true, bool recoverSenderOnly = false)
        {
            if (receipts is null || receipts.Length == 0) return false;

            if (recoverSenderOnly) return (forceRecoverSender && receipts[0].Sender is null);
            return (receipts[0].BlockHash is null || (forceRecoverSender && receipts[0].Sender is null));
        }

        private class RecoveryContext : IReceiptsRecovery.IRecoveryContext
        {
            private readonly IReleaseSpec _releaseSpec;
            private readonly Block _block;
            private readonly bool _forceRecoverSender;
            private readonly IEthereumEcdsa _ecdsa;

            private long _gasUsedBefore = 0;
            private int _transactionIndex = 0;

            public RecoveryContext(IReleaseSpec releaseSpec, Block block, bool forceRecoverSender, IEthereumEcdsa ecdsa)
            {
                _releaseSpec = releaseSpec;
                _block = block;
                _forceRecoverSender = forceRecoverSender;
                _ecdsa = ecdsa;
            }

            public void RecoverReceiptData(TxReceipt receipt)
            {
                if (_transactionIndex >= _block.Transactions.Length)
                {
                    throw new InvalidOperationException("Trying to recover more receipt that transaction");
                }

                Transaction transaction = _block.Transactions[_transactionIndex];

                receipt.BlockHash = _block.Hash;
                receipt.BlockNumber = _block.Number;
                receipt.TxHash = transaction.Hash;
                receipt.Index = _transactionIndex;
                receipt.Sender = transaction.SenderAddress ?? (_forceRecoverSender ? _ecdsa.RecoverAddress(transaction, !_releaseSpec.ValidateChainId) : null);
                receipt.Recipient = transaction.IsContractCreation ? null : transaction.To;

                // how would it be in CREATE2?
                receipt.ContractAddress = transaction.IsContractCreation && transaction.SenderAddress is not null ? ContractAddress.From(receipt.Sender, transaction.Nonce) : null;
                receipt.GasUsed = receipt.GasUsedTotal - _gasUsedBefore;
                if (receipt.StatusCode != StatusCode.Success)
                {
                    receipt.StatusCode = (receipt.Logs?.Length ?? 0) == 0 ? StatusCode.Failure : StatusCode.Success;
                }

                IncrementContext(receipt.GasUsedTotal);
            }

            public void RecoverReceiptData(ref TxReceiptStructRef receipt)
            {
                if (_transactionIndex >= _block.Transactions.Length)
                {
                    throw new InvalidOperationException("Trying to recover more receipt that transaction");
                }

                Transaction transaction = _block.Transactions[_transactionIndex];

                receipt.BlockHash = _block.Hash!.ToStructRef();
                receipt.BlockNumber = _block.Number;
                receipt.TxHash = transaction.Hash!.ToStructRef();
                receipt.Index = _transactionIndex;
                receipt.Sender = (transaction.SenderAddress ?? (_forceRecoverSender ? _ecdsa.RecoverAddress(transaction, !_releaseSpec.ValidateChainId) : Address.Zero))!.ToStructRef();
                receipt.Recipient = (transaction.IsContractCreation ? Address.Zero : transaction.To)!.ToStructRef();

                // how would it be in CREATE2?
                receipt.ContractAddress = (transaction.IsContractCreation && transaction.SenderAddress is not null ? ContractAddress.From(receipt.Sender.ToAddress(), transaction.Nonce) : Address.Zero)!.ToStructRef();
                receipt.GasUsed = receipt.GasUsedTotal - _gasUsedBefore;
                if (receipt.StatusCode != StatusCode.Success)
                {
                    receipt.StatusCode = (receipt.Logs?.Length ?? 0) == 0 ? StatusCode.Failure : StatusCode.Success;
                }

                IncrementContext(receipt.GasUsedTotal);
            }

            private void IncrementContext(long gasUsedTotal)
            {
                _transactionIndex++;
                _gasUsedBefore = gasUsedTotal;
            }
        }
    }
}
