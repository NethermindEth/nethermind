// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Whether an authenticated broadcast manifest may start or join a session.</summary>
internal enum LeanBroadcastAdmission
{
    /// <summary>Coded state is charged and, for kind 3, the shared assembly exists.</summary>
    Started,

    /// <summary>The object is already held and validated, so its shards can be served from the body.</summary>
    Held,

    /// <summary>The object is known invalid or the transport is inactive.</summary>
    Rejected,

    /// <summary>Local budgets are exhausted; not peer misconduct.</summary>
    Busy
}

public sealed partial class LeanObjectTransport
{
    private const int MaxBroadcastSidecars = 16;
    private readonly Dictionary<ValueHash256, (LeanDescriptor Descriptor, byte[] Proof)> _broadcastSidecars = [];
    private readonly Queue<ValueHash256> _broadcastSidecarOrder = [];

    /// <summary>The chain ID and genesis hash that scope manifest IDs and channels, or null while the genesis is unknown.</summary>
    internal (UInt256 ChainId, ValueHash256 Genesis)? BroadcastScope => CreateStatus() is { } status ? (status.ChainId, status.GenesisHash) : null;

    /// <summary>The existing descriptor, skeleton and applicable-fork checks a manifest must pass before it is accepted.</summary>
    internal string? CheckBroadcastDescriptor(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton)
    {
        if (descriptor.ProfileId != LocalProfile) return "profile is not common";
        if ((LocalKinds & (1 << (descriptor.Kind - 1))) == 0) return "kind is not supported";
        if (descriptor.ByteLength > LeanLimits.MaxObjectBytes) return "object exceeds max_object_bytes";
        if (descriptor.Kind == LeanProtocol.KindBlockProof
            && (skeleton is null || !skeleton.TryValidate(descriptor, _headerDecoder, _specProvider, out string? error))) return "invalid header skeleton";
        return null;
    }

    /// <summary>Charges a broadcast's coded state and, for kind 3, starts the object's shared assembly if none exists.</summary>
    /// <remarks>EIP-8437: all bindings and manifests contribute to one assembly per object ID with one absolute deadline;
    /// requested retrieval can recover the object within it if the broadcast fails.</remarks>
    internal LeanBroadcastAdmission AdmitBroadcast(LeanDescriptor descriptor, long codedBytes)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_disposed || !IsEnabled || _tombstones.Contains(descriptor.ObjectId, now)) return LeanBroadcastAdmission.Rejected;
            if (_store.Contains(descriptor.ObjectId)) return LeanBroadcastAdmission.Held;
            if (codedBytes > LeanLimits.MaxIncompleteBytes - _incompleteBytes) return LeanBroadcastAdmission.Busy;
            if (descriptor.Kind == LeanProtocol.KindInclusionList && !_assemblies.ContainsKey(descriptor.ObjectId))
            {
                if (TryCreateAssembly(descriptor, null, null, now) is not { } assembly) return LeanBroadcastAdmission.Busy;
                assembly.Validate = true;
            }
            _incompleteBytes += codedBytes;
            return LeanBroadcastAdmission.Started;
        }
    }

    internal void ReleaseBroadcast(long codedBytes)
    {
        lock (_gate) _incompleteBytes -= codedBytes;
    }

    /// <summary>The body of a held, fully validated object, so a node that already has it can serve its shards.</summary>
    internal byte[]? HeldBody(in ValueHash256 objectId)
    {
        lock (_gate) return _store.TryGet(objectId, out LeanObjectStore.Entry entry) ? entry.Body : null;
    }

    /// <summary>Applies the canonical object checks to a body reconstructed from broadcast shards, then validates it.</summary>
    /// <param name="started">The <see cref="Stopwatch"/> timestamp at which the broadcast session was accepted.</param>
    /// <returns>Null when the body enters validation or is refused locally; otherwise the producer fault, which does not
    /// tombstone the object: a broadcast failure is scoped to its manifest.</returns>
    /// <remarks>
    /// A kind-3 package completes its shared assembly and goes through the same validation queue as retrieved objects, so an
    /// invalid package is rejected as such. A kind-2 proof that rebuilds the header committed by the block hash is kept for
    /// block import, which still checks it against the block's dependencies.
    /// </remarks>
    internal string? AcceptBroadcastBody(LeanDescriptor descriptor, LeanHeaderSkeleton? skeleton, byte[] body, long started)
    {
        List<LeanBodies.WrapperEntry>? entries = null;
        if (CheckIntegrity(descriptor, body, ref entries) is { } integrityError) return integrityError;
        if (descriptor.Kind == LeanProtocol.KindBlockProof)
        {
            byte[] proof = LeanBodies.ParseBlockProof(body).ToArray();
            if (ValueKeccak.Compute(skeleton!.Reconstruct(proof, descriptor.BlockDepsHash)) != descriptor.BlockHash)
                return "proof does not rebuild the header of its block hash";
            lock (_gate)
            {
                if (!_broadcastSidecars.ContainsKey(descriptor.BlockHash))
                {
                    if (_broadcastSidecarOrder.Count >= MaxBroadcastSidecars && _broadcastSidecarOrder.TryDequeue(out ValueHash256 oldest))
                        _broadcastSidecars.Remove(oldest);
                    _broadcastSidecarOrder.Enqueue(descriptor.BlockHash);
                }
                _broadcastSidecars[descriptor.BlockHash] = (descriptor, proof);
                TaskCompletionSource signal = _hintSignal;
                _hintSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                signal.TrySetResult();
            }
            LeanMetrics.Transferred(LeanProtocol.KindBlockProof, broadcast: true, Stopwatch.GetElapsedTime(started));
            return null;
        }
        return QueueBroadcastPackage(descriptor, body, started);
    }

    /// <summary>Completes the package's shared assembly with the reconstructed body, so it is validated once, in the bounded queue.</summary>
    /// <remarks>Requested retrieval of the same object stops; a body retrieval already completed is validated instead.</remarks>
    private string? QueueBroadcastPackage(LeanDescriptor descriptor, byte[] body, long started)
    {
        Outbox outbox = new();
        lock (_gate)
        {
            if (_disposed || _store.Contains(descriptor.ObjectId)) return null;
            long now = _clock.GetTimestamp();
            if (!_assemblies.TryGetValue(descriptor.ObjectId, out LeanAssembly? assembly))
            {
                if (_tombstones.Contains(descriptor.ObjectId, now)) return null;
                assembly = TryCreateAssembly(descriptor, null, null, now);
                if (assembly is null) return LocalRefusal("no room for its assembly");
                assembly.Validate = true;
            }
            if (assembly.Queued || assembly.Dropped) return null;
            long length = (long)descriptor.ByteLength;
            if (_pendingValidations >= LeanLimits.MaxPendingValidations || length > LeanLimits.MaxCompletionBytes - _completionBytes)
                return LocalRefusal("validation queue full");
            _completionBytes += length;
            assembly.Validate = true;
            assembly.BroadcastStarted = started;
            Enqueue(assembly, body, outbox);
        }
        outbox.Flush();
        return null;
    }

    private string? LocalRefusal(string reason)
    {
        // Not a producer fault: the assembly, if any, stays available to requested retrieval.
        if (_logger.IsDebug) _logger.Debug($"lean/1 broadcast body not queued: {reason}");
        return null;
    }

    internal bool HasBroadcastSidecar(in ValueHash256 blockHash) { lock (_gate) return _broadcastSidecars.ContainsKey(blockHash); }

    /// <summary>A broadcast sidecar matching the importing block's number, transactions root and dependency hash.</summary>
    private RecursiveStark? BroadcastSidecar(in ValueHash256 blockHash, Block block, in ValueHash256 depsHash) =>
        _broadcastSidecars.TryGetValue(blockHash, out (LeanDescriptor Descriptor, byte[] Proof) sidecar)
        && sidecar.Descriptor.BlockNumber == block.Number && sidecar.Descriptor.TransactionsRoot == block.Header.TxRoot!.ValueHash256
        && sidecar.Descriptor.BlockDepsHash == depsHash
            ? new RecursiveStark(sidecar.Proof, new Hash256(depsHash))
            : null;
}
