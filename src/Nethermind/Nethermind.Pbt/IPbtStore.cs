// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>Destination of complete node-group replacements; stores accept them only through an <see cref="IPbtConcurrentWriter"/>.</summary>
public interface IPbtNodeGroupSink
{
    /// <summary>Replaces or deletes the complete group identified by <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// The path is borrowed only for this call; retained identities must be immutable snapshots.
    /// A null payload deletes the group. A non-null payload is borrowed for the call; the caller retains
    /// its reference and must release it. Implementations retaining the immutable payload acquire their
    /// own reference before replacing the previous payload. Invalid keys or payloads leave the group unchanged.
    /// A null payload can delete an empty physical group while its logical subtree survives compression.
    /// </remarks>
    void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload);

    /// <summary>Reserves a replacement's final payload and hands its reference off with this writer; zero length deletes the group.</summary>
    /// <remarks>
    /// The caller fills the borrowed span before publishing its reference to a parent. The store keeps the reservation
    /// alive through fold completion and publication. Payload-only sinks return Unknown and receive SetNodeGroup instead.
    /// </remarks>
    PbtGroupReference ReserveNodeGroup(scoped in PbtTraversalPath groupKey, int length, in PbtGroupLinks links, out Span<byte> payload)
    {
        payload = default;
        return PbtGroupReference.Unknown;
    }
}

/// <summary>Backing store used by <see cref="TrieUpdater"/> for canonical complete-key mutations.</summary>
public interface IPbtStore
{
    /// <summary>The input root's group reference, None for a known empty root, or Unknown when unavailable.</summary>
    PbtGroupReference RootGroupReference => PbtGroupReference.Unknown;

    /// <summary>Borrows a direct record for this path, or returns false to use the exact store lookup.</summary>
    /// <remarks>The store protects the record's lifetime through worker completion and publication.</remarks>
    bool TryGetNodeGroupRecord(PbtGroupReference reference, scoped in PbtTraversalPath groupKey, out PbtGroupRecord record)
    {
        record = default;
        return false;
    }

    /// <summary>Marks a successful root fold, allowing stores to publish its handed-off writes on disposal.</summary>
    void CompleteFold() { }

    /// <summary>Gets a caller-owned lease for the complete group identified by <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// A non-null result transfers exactly one reference to the caller, which must invoke
    /// <see cref="IDisposable.Dispose"/> exactly once. Consumers must treat the payload as immutable.
    /// The reference keeps the payload valid until released, independently of the store. A missing group
    /// returns <see langword="null"/>. Implementations validate group boundary keys even on misses.
    /// The path is borrowed only for this call; retained identities must be immutable snapshots,
    /// never aliases of the cursor buffer.
    /// </remarks>
    RefCountingMemory? GetNodeGroup(scoped in PbtTraversalPath groupKey);

    /// <summary>Creates a writer for one worker; every group replacement reaches this store through one.</summary>
    /// <remarks>
    /// A writer is not thread-safe, but any number of them may be used concurrently, and each may be created and
    /// disposed on any thread. When a disposed writer's groups become visible through this store is up to the store.
    /// </remarks>
    IPbtConcurrentWriter CreateWriter();
}

/// <summary>A single-worker writer created by <see cref="IPbtStore.CreateWriter"/>; disposing it hands its writes to the store.</summary>
public interface IPbtConcurrentWriter : IPbtNodeGroupSink, IDisposable;

/// <summary>Forwards every write immediately, for stores whose own sink accepts concurrent writes.</summary>
public sealed class PbtPassThroughWriter(IPbtNodeGroupSink store) : IPbtConcurrentWriter
{
    /// <inheritdoc/>
    public PbtGroupReference ReserveNodeGroup(scoped in PbtTraversalPath groupKey, int length, in PbtGroupLinks links, out Span<byte> payload) =>
        store.ReserveNodeGroup(groupKey, length, links, out payload);

    public void SetNodeGroup(scoped in PbtTraversalPath groupKey, RefCountingMemory? payload) =>
        store.SetNodeGroup(groupKey, payload);

    public void Dispose() { }
}
