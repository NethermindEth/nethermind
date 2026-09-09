// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

internal static class BlobTransactionPayload
{
    public static Transaction Elide(Transaction transaction)
    {
        if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
        {
            return transaction;
        }

        if (wrapper.Blobs.Length == 0 && wrapper.CellMask.IsEmpty && wrapper.Cells is null)
        {
            return transaction;
        }

        Transaction elided = new();
        transaction.CopyTo(elided, copyHash: true);
        elided.NetworkWrapper = wrapper with { Blobs = [], CellMask = default, Cells = null };
        elided.ClearLengthCache();
        return elided;
    }
}
