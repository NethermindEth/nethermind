// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>
/// One diff layer of state changes: flat entries keyed by raw address/slot, plus the complete
/// post-change leaf blobs and stem trie nodes produced by the block's root computation.
/// </summary>
/// <remarks>
/// Account/slot maps are concurrent because block processing may populate them from parallel
/// write batches; blobs and nodes are written single-threaded at commit. Conventions: a null
/// account = deleted; a null slot value = zero; an empty blob = stem deleted; a null node = removed.
/// </remarks>
public class PbtSnapshotContent
{
    public ConcurrentDictionary<AddressAsKey, Account?> Accounts { get; } = new();
    public ConcurrentDictionary<(AddressAsKey Address, UInt256 Slot), byte[]?> Slots { get; } = new();
    public ConcurrentDictionary<AddressAsKey, bool> SelfDestructs { get; } = new();
    public Dictionary<Stem, byte[]> LeafBlobs { get; } = [];
    public Dictionary<TrieNodeKey, byte[]?> TrieNodes { get; } = [];
}
