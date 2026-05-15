// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Captured result of a speculatively-executed transaction by the prewarmer.
/// If the main thread validates that inputs haven't changed, it can adopt
/// this result instead of re-executing the EVM.
/// </summary>
internal sealed class SpeculativeTxResult
{
    /// <summary>Gas used by the transaction.</summary>
    public long GasUsed;

    /// <summary>Whether the tx executed successfully.</summary>
    public bool Success;

    /// <summary>Completion flag — set when the prewarmer finishes this tx.</summary>
    public volatile bool IsComplete;

    /// <summary>
    /// Account state after tx execution: address → (balance, nonce, codeHash, storageRoot).
    /// Used for validation: if any of these differ from main thread's current state
    /// at the point of adoption, the result is stale.
    /// </summary>
    public Dictionary<AddressAsKey, AccountSnapshot>? AccountChanges;

    /// <summary>
    /// Storage changes: (address, slot) → new value.
    /// Applied to main thread's WorldState if the result is adopted.
    /// </summary>
    public Dictionary<StorageKey, byte[]>? StorageChanges;

    public readonly record struct AccountSnapshot(Account? Account);

    public readonly record struct StorageKey(Address Address, Nethermind.Int256.UInt256 Index);
}
