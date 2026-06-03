// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Wraps the code <see cref="IKeyValueStoreWithBatching"/> used during witness re-execution and
/// records every observed key/value pair as a code preimage for inclusion in the witness.
/// </summary>
/// <remarks>
/// <para>
/// The witness must contain only the codes that existed in the pre-state and were touched by
/// block execution. Codes deployed during this block (via CREATE / CREATE2 or EIP-7702 SetCode)
/// must NOT appear in the witness, because the stateless verifier reconstructs them by re-running
/// the block.
/// </para>
/// <para>
/// This distinction is upheld structurally: <see cref="Nethermind.State.StateProvider.InsertCode"/>
/// stores newly-inserted code in an in-memory write batch (<c>_codeBatchAlternate</c>) that
/// <see cref="Nethermind.State.StateProvider"/> consults <em>before</em> falling back to the
/// underlying code DB. Reads of in-block-deployed code therefore hit the batch and never reach
/// the wrapped store, while reads of pre-state codes miss the batch and route here. Every <see cref="Get"/>
/// the wrapper observes is, by construction, a pre-state code load.
/// </para>
/// <para>
/// Only <see cref="Get"/> carries the capture logic. The interface's default implementations of
/// the indexer, <c>GetSpan</c>, and <c>GetOwnedMemory</c> all route through <see cref="Get"/> via
/// virtual dispatch, so a single override is sufficient. <see cref="KeyExists"/> is forwarded
/// directly to the inner store so that presence-only checks (notably
/// <c>StateProvider.InsertCode</c>'s dedupe against the underlying DB) do not record bytecode.
/// </para>
/// </remarks>
public sealed class WitnessCapturingCodeDb(IKeyValueStoreWithBatching inner) : IKeyValueStoreWithBatching
{
    private readonly ConcurrentDictionary<ValueHash256, byte[]> _captured = new();

    /// <summary>The pre-state code preimages observed during witness re-execution.</summary>
    public IEnumerable<byte[]> CapturedCodes
    {
        get
        {
            foreach (KeyValuePair<ValueHash256, byte[]> kvp in _captured)
                yield return kvp.Value;
        }
    }

    /// <summary>Number of distinct code preimages observed so far.</summary>
    public int Count => _captured.Count;

    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        => Capture(key, inner.Get(key, flags));

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        => inner.Set(key, value, flags);

    public IWriteBatch StartWriteBatch() => inner.StartWriteBatch();

    private byte[]? Capture(ReadOnlySpan<byte> key, byte[]? value)
    {
        if (value is { Length: > 0 } && key.Length == ValueHash256.MemorySize)
            _captured.TryAdd(new ValueHash256(key), value);
        return value;
    }
}
