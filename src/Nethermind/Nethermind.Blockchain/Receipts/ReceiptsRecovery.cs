// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Receipts
{
    public class ReceiptsRecovery : IReceiptsRecovery
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ISpecProvider _specProvider;

        public ReceiptsRecovery(IEthereumEcdsa? ecdsa, ISpecProvider? specProvider)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        public ReceiptsRecoveryResult TryRecover(Block block, TxReceipt[] receipts, bool forceRecoverSender = true)
        {
            var canRecover = block.Transactions.Length == receipts?.Length;
            if (canRecover)
            {
                var needRecover = NeedRecover(receipts, forceRecoverSender);
                if (needRecover)
                {
                    var releaseSpec = _specProvider.GetSpec(block.Header);
                    long gasUsedBefore = 0;
                    for (int receiptIndex = 0; receiptIndex < block.Transactions.Length; receiptIndex++)
                    {
                        Transaction transaction = block.Transactions[receiptIndex];
                        if (receipts.Length > receiptIndex)
                        {
                            TxReceipt receipt = receipts[receiptIndex];
                            RecoverReceiptData(releaseSpec, receipt, block, transaction, receiptIndex, gasUsedBefore, forceRecoverSender);
                            gasUsedBefore = receipt.GasUsedTotal;
                        }
                    }

                    return ReceiptsRecoveryResult.Success;
                }

                return ReceiptsRecoveryResult.Skipped;
            }

            return ReceiptsRecoveryResult.Fail;
        }

        public bool NeedRecover(TxReceipt[] receipts, bool forceRecoverSender = true) => receipts?.Length > 0 && (receipts[0].BlockHash is null || (forceRecoverSender && receipts[0].Sender is null));

        private void RecoverReceiptData(IReleaseSpec releaseSpec, TxReceipt receipt, Block block, Transaction transaction, int transactionIndex, long gasUsedBefore, bool force)
        {
            receipt.BlockHash = block.Hash;
            receipt.BlockNumber = block.Number;
            receipt.TxHash = transaction.Hash;
            receipt.Index = transactionIndex;
            receipt.Sender = transaction.SenderAddress ?? (force ? _ecdsa.RecoverAddress(transaction, !releaseSpec.ValidateChainId) : null);
            receipt.Recipient = transaction.IsContractCreation ? null : transaction.To;

            // how would it be in CREATE2?
            receipt.ContractAddress = transaction.IsContractCreation && transaction.SenderAddress is not null ? ContractAddress.From(receipt.Sender, transaction.Nonce) : null;
            receipt.GasUsed = receipt.GasUsedTotal - gasUsedBefore;
            if (receipt.StatusCode != StatusCode.Success)
            {
                receipt.StatusCode = receipt.Logs.Length == 0 ? StatusCode.Failure : StatusCode.Success;
            }
        }
    }
}
