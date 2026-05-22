// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

/// <summary>
/// Persists <see cref="IStateBoundary.OldestStateBlock"/> in a key-value store provided by
/// the world-state-manager implementation. Trie storage uses the state DB; flat storage uses
/// the flat DB's metadata column. Either way the floor is co-located with the state itself,
/// so dropping the state directory drops the floor automatically.
/// </summary>
public sealed class StateBoundaryStore(IKeyValueStore kv)
{
    /// <summary>
    /// Slot key under which the oldest-state-block floor is persisted. 32-byte keccak makes it
    /// collision-free against the trie DB's hash-keyed (32 bytes of state-root-hash entropy)
    /// and HalfPath-keyed nodes, and matches the existing flat metadata-column convention
    /// (<c>Keccak.Compute("CurrentState")</c>, <c>Keccak.Compute("Layout")</c>).
    /// </summary>
    internal static readonly byte[] OldestStateBlockKey = Keccak.Compute("OldestStateBlock").BytesToArray();

    private readonly Lock _lock = new();
    private long? _value = kv[OldestStateBlockKey]?.AsRlpValueContext().DecodeLong();

    public long? OldestStateBlock
    {
        get
        {
            lock (_lock) return _value;
        }
        set
        {
            lock (_lock)
            {
                if (_value == value) return;
                // Reject backward non-null writes; null reset is permitted for recovery.
                if (value.HasValue && _value.HasValue && value.Value < _value.Value) return;
                // Persist before caching so a thrown kv write doesn't desync memory from disk.
                if (value.HasValue)
                    kv[OldestStateBlockKey] = Rlp.Encode(value.Value).Bytes;
                else
                    kv.Remove(OldestStateBlockKey);
                _value = value;
            }
        }
    }
}
