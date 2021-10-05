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
        
        public bool TryRecover(Block block, TxReceipt[] receipts)
        {
            var canRecover = block.Transactions.Length == receipts?.Length;
            if (canRecover)
            {
                var needRecover = NeedRecover(receipts);
                if (needRecover)
                {
                    var releaseSpec = _specProvider.GetSpec(block.Number);
                    long gasUsedBefore = 0;
                    for (int receiptIndex = 0; receiptIndex < block.Transactions.Length; receiptIndex++)
                    {
                        Transaction transaction = block.Transactions[receiptIndex];
                        if (receipts.Length > receiptIndex)
                        {
                            TxReceipt receipt = receipts[receiptIndex];
                            RecoverReceiptData(releaseSpec, receipt, block, transaction, receiptIndex, gasUsedBefore);
                            gasUsedBefore = receipt.GasUsedTotal;
                        }
                    }
                }

                return true;
            }

            return false;
        }
        
        public bool NeedRecover(TxReceipt[] receipts) => receipts?.Length > 0 && (receipts[0].BlockHash == null || receipts[0].Sender == null);

        private void RecoverReceiptData(IReleaseSpec releaseSpec, TxReceipt receipt, Block block, Transaction transaction, int transactionIndex, long gasUsedBefore)
        {
            receipt.BlockHash = block.Hash;
            receipt.BlockNumber = block.Number;
            receipt.TxHash = transaction.Hash;
            receipt.Index = transactionIndex;
            receipt.Sender = transaction.SenderAddress ?? _ecdsa.RecoverAddress(transaction, !releaseSpec.ValidateChainId);
            receipt.Recipient = transaction.IsContractCreation ? null : transaction.To;
            
            // how would it be in CREATE2?
            receipt.ContractAddress = transaction.IsContractCreation ? ContractAddress.From(receipt.Sender, transaction.Nonce) : null; 
            receipt.GasUsed = receipt.GasUsedTotal - gasUsedBefore;
            if (receipt.StatusCode != StatusCode.Success)
            {
                receipt.StatusCode = receipt.Logs.Length == 0 ? StatusCode.Failure : StatusCode.Success;
            }
        }
    }
}
