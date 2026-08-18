// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Xdc.TxPool;

internal static class XdcAcceptTxResult
{
    // Ids outside the range used by AcceptTxResult's own values, which are compared by id.
    // Deliberately not AcceptTxResult.Invalid: TxFloodController disconnects a peer that relays an
    // Invalid transaction, and pool admission is judged against the local head, so two honest nodes
    // one block apart across BlackListHFNumber would drop each other.
    private const int BlackListedAddressId = 1000;

    public static AcceptTxResult BlackListedAddress { get; } = new(BlackListedAddressId, nameof(BlackListedAddress));
}
