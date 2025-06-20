// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using Nethermind.TxPool.Filters;

namespace Nethermind.Consensus.Transactions;

public class BlobLimitTxFilter : ITxFilter
{
    public AcceptTxResult IsAllowed(Transaction tx, BlockHeader parentHeader, IReleaseSpec spec)
        => !tx.SupportsBlobs || spec.MaxBlobsPerTx >= (ulong)tx.BlobVersionedHashes!.Length ? AcceptTxResult.Accepted : AcceptTxResult.Invalid;
}
