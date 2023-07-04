// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class FullInfoReceiptFinder : IReceiptFinder
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly IBlockFinder _blockFinder;

        public FullInfoReceiptFinder(IReceiptStorage receiptStorage, IReceiptsRecovery receiptsRecovery, IBlockFinder blockFinder)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }

        public Keccak FindBlockHash(Keccak txHash) => _receiptStorage.FindBlockHash(txHash);

        public TxReceipt[] Get(Block block)
        {
            var receipts = _receiptStorage.Get(block);
            if (_receiptsRecovery.TryRecover(block, receipts) == ReceiptsRecoveryResult.NeedReinsert)
            {
                _receiptStorage.Insert(block, receipts);
            }

            return receipts;
        }

        public TxReceipt[] Get(Keccak blockHash)
        {
            var receipts = _receiptStorage.Get(blockHash);

            if (_receiptsRecovery.NeedRecover(receipts))
            {
                var block = _blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (_receiptsRecovery.TryRecover(block, receipts) == ReceiptsRecoveryResult.NeedReinsert)
                {
                    _receiptStorage.Insert(block, receipts);
                }
            }

            return receipts;
        }

        public bool CanGetReceiptsByHash(long blockNumber) => _receiptStorage.CanGetReceiptsByHash(blockNumber);
        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator) => _receiptStorage.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);
    }
}
