// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Taiko;

public interface IL1OriginStore
{
    L1Origin? ReadL1Origin(UInt256 blockId);
    void WriteL1Origin(UInt256 blockId, L1Origin l1Origin);

    UInt256? ReadHeadL1Origin();
    void WriteHeadL1Origin(UInt256 blockId);

    UInt256? ReadBatchToLastBlockID(UInt256 batchId);
    void WriteBatchToLastBlockID(UInt256 batchId, UInt256 blockId);

    /// <summary>
    /// Atomically attaches <paramref name="signature"/> to the L1Origin record for
    /// <paramref name="blockId"/> and persists it. The read–modify–write is serialised
    /// against other writes to the store so concurrent <c>taikoAuth_setL1OriginSignature</c>
    /// or <c>taikoAuth_updateL1Origin</c> calls cannot clobber each other.
    /// </summary>
    /// <returns>The updated origin, or <c>null</c> if no record exists for <paramref name="blockId"/>.</returns>
    L1Origin? SetL1OriginSignature(UInt256 blockId, byte[] signature);
}
