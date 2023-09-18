// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool : TxDistinctSortedPool
{
    private readonly ILogger _logger;

    public BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
        : base(capacity, comparer, logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    }

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public override void EnsureCapacity()
    {
        if (Count > _poolCapacity && _logger.IsWarn)
            _logger.Warn($"Blob pool exceeds the config size {Count}/{_poolCapacity}");
    }

    /// <summary>
    /// For tests only - to test sorting
    /// </summary>
    internal void TryGetBlobTxSortingEquivalent(Keccak hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);
}
