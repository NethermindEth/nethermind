// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts;

public partial class PersistentReceiptStorage
{
    private ReceiptsIterator CreateRecoveryIterator(Span<byte> receiptsData, ulong blockNumber, Hash256 blockHash, IReceiptRefDecoder refDecoder)
    {
        Func<IReceiptsRecovery.IRecoveryContext?> recoveryContextFactory = static () => null;
        Func<IReceiptsRecovery.IRecoveryContext?>? logRecoveryContextFactory = null;
        if (ReceiptArrayStorageDecoder.IsCompactEncoding(receiptsData))
        {
            recoveryContextFactory = () => _receiptsRecovery.CreateRecoveryContext(GetRecoveryBlock(blockNumber, blockHash));
            logRecoveryContextFactory = () => _receiptsRecovery.CreateLogRecoveryContext(GetRecoveryBlock(blockNumber, blockHash));
        }

        return new ReceiptsIterator(receiptsData, _receiptsDb, recoveryContextFactory, refDecoder, logRecoveryContextFactory);
    }

    private readonly struct RecoveryBlockSource(PersistentReceiptStorage storage, ulong blockNumber, Hash256 blockHash)
        : IReceiptsRecovery.IRecoveryBlockSource
    {
        public ReceiptRecoveryBlock GetBlock() => storage.GetRecoveryBlock(blockNumber, blockHash);
    }
}
