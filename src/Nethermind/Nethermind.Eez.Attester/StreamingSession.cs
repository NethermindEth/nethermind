// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingSession
{
    private readonly object _lock = new();
    private readonly string _owner;
    private readonly StreamingSessionLimits _limits;
    private readonly List<RetainedBlock> _blocks = [];
    private Begin _anchor;
    private SessionBinding _binding;
    private SessionOperation? _pending;
    private long _bytes;
    private long _witnessItems;
    private bool _cancelled;
    private TaskCompletionSource _revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ulong RollupId { get; }
    internal ByteString Id { get; } = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));

    internal StreamingSession(string authenticatedOwner, Begin begin, StreamingSessionLimits limits)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticatedOwner);
        limits.Validate();
        if (begin.RollupId == 0 || begin.AnchorNumber == ulong.MaxValue ||
            begin.AnchorHash.Length != 32 || begin.AnchorStateRoot.Length != 32)
        {
            throw Error(StatusCode.InvalidArgument, "invalid session anchor");
        }
        _owner = authenticatedOwner;
        RollupId = begin.RollupId;
        _anchor = new Begin
        {
            RollupId = begin.RollupId,
            AnchorNumber = begin.AnchorNumber,
            AnchorHash = begin.AnchorHash,
            AnchorStateRoot = begin.AnchorStateRoot,
        };
        _limits = limits;
        _binding = new SessionBinding(Guid.NewGuid(), 1);
    }

    internal SessionBinding Binding
    {
        get { lock (_lock) return _binding; }
    }

    internal void CheckCurrent(SessionBinding binding)
    {
        lock (_lock) CheckBinding(binding);
    }

    internal Task AttachmentRevoked(SessionBinding binding)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            return _revoked.Task;
        }
    }

    internal bool CanRelease
    {
        get { lock (_lock) return _cancelled && _pending is null; }
    }

    internal void Retire()
    {
        lock (_lock)
        {
            _cancelled = true;
            _revoked.TrySetResult();
            RemoveSuffix(0);
        }
    }

    internal ServerFrame Ready(SessionBinding binding, ulong requestId)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            return Frame(requestId, new Ready { ValidatedThrough = TipNumber, ValidatedHash = TipHash });
        }
    }

    internal SessionBinding Resume(string authenticatedOwner)
    {
        lock (_lock)
        {
            if (_cancelled || !StringComparer.Ordinal.Equals(_owner, authenticatedOwner))
                throw Error(StatusCode.NotFound, "session unavailable");
            AdvanceEpoch();
            return _binding;
        }
    }

    internal ServerFrame Rewind(SessionBinding binding, ulong requestId, ByteString ancestorHash) =>
        RewindAttachment(binding, requestId, ancestorHash).Response;

    internal (ServerFrame Response, SessionBinding Binding) RewindAttachment(SessionBinding binding, ulong requestId, ByteString ancestorHash)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            int index = FindAncestor(ancestorHash);
            if (index == -2) throw Error(StatusCode.FailedPrecondition, "ancestor outside validated prefix");
            AdvanceEpoch();
            RemoveSuffix(index + 1);
            return (Frame(requestId, new Ready { ValidatedThrough = TipNumber, ValidatedHash = TipHash }), _binding);
        }
    }

    internal ServerFrame Cancel(SessionBinding binding, ulong requestId)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            ServerFrame response = new() { SessionId = Id, RequestId = requestId, Epoch = binding.Epoch, Cancelled = new Cancelled() };
            _cancelled = true;
            _revoked.TrySetResult();
            RemoveSuffix(0);
            return response;
        }
    }

    internal SessionOperation AdmitBlock(SessionBinding binding, ulong requestId, BlockWitness block)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            CheckIdle();
            if (block.Witness is null || block.Rlp.IsEmpty || block.Hash.Length != 32 || block.ParentHash.Length != 32)
                throw Error(StatusCode.InvalidArgument, "invalid block witness");
            int duplicate = FindBlock(block.Number);
            if (duplicate >= 0)
            {
                BlockWitness retained = _blocks[duplicate].Data;
                if (retained.Hash != block.Hash || retained.ParentHash != block.ParentHash || retained.Rlp != block.Rlp)
                    throw Error(StatusCode.FailedPrecondition, "different block at validated height");
            }
            else if (TipNumber == ulong.MaxValue || block.Number != TipNumber + 1 || block.ParentHash != TipHash)
                throw Error(StatusCode.FailedPrecondition, "block outside next validated height");

            long size = block.CalculateSize();
            long items = (long)block.Witness.State.Count + block.Witness.Codes.Count + block.Witness.Keys.Count + block.Witness.Headers.Count;
            if (size > _limits.MaxBytes - _bytes || items > _limits.MaxWitnessItems - _witnessItems ||
                (duplicate < 0 && _blocks.Count >= _limits.MaxBlocks))
                throw Error(StatusCode.ResourceExhausted, "session block limit exceeded");

            ByteString preStateRoot = duplicate >= 0 ? PreStateRoot(duplicate) : TipStateRoot;
            _pending = new SessionOperation(this, binding, requestId, block.Clone(), preStateRoot, size, items, duplicate);
            return _pending;
        }
    }

    internal SessionOperation AdmitFinalize(SessionBinding binding, ulong requestId, Finalize finalize)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            CheckIdle();
            if (finalize.Settlement is null || finalize.TerminalHash.Length != 32 || finalize.FromBlock > finalize.ToBlock)
                throw Error(StatusCode.InvalidArgument, "invalid finalization range or settlement");
            int first = FindBlock(finalize.FromBlock);
            int last = FindBlock(finalize.ToBlock);
            if (first < 0 || last < first || _blocks[last].Data.Hash != finalize.TerminalHash)
                throw Error(StatusCode.FailedPrecondition, "range outside validated prefix");
            if (finalize.CalculateSize() > _limits.MaxBytes - _bytes)
                throw Error(StatusCode.ResourceExhausted, "session settlement limit exceeded");
            BlockWitness[] blocks = new BlockWitness[last - first + 1];
            for (int i = 0; i < blocks.Length; i++) blocks[i] = _blocks[first + i].Data.Clone();
            Begin predecessor = new()
            {
                RollupId = _anchor.RollupId,
                AnchorNumber = finalize.FromBlock - 1,
                AnchorHash = blocks[0].ParentHash,
                AnchorStateRoot = PreStateRoot(first),
            };
            _pending = new SessionOperation(this, binding, requestId, finalize.Clone(), blocks, predecessor);
            return _pending;
        }
    }

    internal ServerFrame CommitBlock(SessionOperation operation, ExecutedStreamingBlock executed)
    {
        lock (_lock)
        {
            CheckOperation(operation);
            BlockWitness block = operation.Block ?? throw new InvalidOperationException("not a block operation");
            if (executed.Number != block.Number || executed.Hash != block.Hash || executed.ParentHash != block.ParentHash ||
                executed.PreStateRoot != operation.PreStateRoot || executed.PostStateRoot.Length != 32)
                throw Error(StatusCode.FailedPrecondition, "executed block differs from admitted block");
            if (operation.Duplicate >= 0)
            {
                if (executed.PostStateRoot != _blocks[operation.Duplicate].PostStateRoot)
                    throw Error(StatusCode.FailedPrecondition, "duplicate execution differs from validated block");
            }
            else
            {
                _blocks.Add(new RetainedBlock(block, executed.PostStateRoot, operation.Size, operation.WitnessItems));
                _bytes += operation.Size;
                _witnessItems += operation.WitnessItems;
            }
            operation.Completed = true;
            return new ServerFrame
            {
                SessionId = Id,
                RequestId = operation.RequestId,
                Epoch = operation.Binding.Epoch,
                Validated = new Validated { Number = block.Number, Hash = block.Hash, PostStateRoot = executed.PostStateRoot },
            };
        }
    }

    internal Proof SignCurrent(SessionOperation operation, EezAttestationSigner signer, ValueHash256 publicInputs, CancellationToken cancellation)
    {
        lock (_lock)
        {
            CheckOperation(operation);
            cancellation.ThrowIfCancellationRequested();
            return new Proof { PublicInputsHash = ByteString.CopyFrom(publicInputs.Bytes), Proof_ = ByteString.CopyFrom(signer.Sign(publicInputs)) };
        }
    }

    internal ServerFrame CompleteProof(SessionOperation operation, Proof proof) => CompleteProofAndFence(operation, proof).Response;

    internal (ServerFrame Response, Task Revoked) CompleteProofAndFence(SessionOperation operation, Proof proof)
    {
        lock (_lock)
        {
            CheckOperation(operation);
            Finalize finalization = operation.Finalize ?? throw new InvalidOperationException("not a finalization operation");
            if (proof.PublicInputsHash.Length != 32 || proof.Proof_.IsEmpty)
                throw Error(StatusCode.Internal, "invalid proof backend result");
            int first = FindBlock(finalization.FromBlock);
            ServerFrame response = new() { SessionId = Id, RequestId = operation.RequestId, Epoch = operation.Binding.Epoch, Proof = proof.Clone() };
            AdvanceEpoch();
            _anchor = operation.Predecessor!;
            for (int i = 0; i < first; i++) Release(_blocks[i]);
            _blocks.RemoveRange(0, first);
            operation.Completed = true;
            return (response, _revoked.Task);
        }
    }

    internal void ReleaseOperation(SessionOperation operation)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_pending, operation))
            {
                operation.Release();
                _pending = null;
            }
        }
    }

    private void CheckOperation(SessionOperation operation)
    {
        CheckBinding(operation.Binding);
        if (!ReferenceEquals(_pending, operation) || operation.Completed || operation.IsDisposed)
            throw Error(StatusCode.Aborted, "operation no longer current");
    }

    private void CheckBinding(SessionBinding binding)
    {
        if (_cancelled || binding != _binding) throw Error(StatusCode.Aborted, "session attachment no longer current");
    }

    private void CheckIdle()
    {
        if (_pending is not null) throw Error(StatusCode.ResourceExhausted, "session worker still active");
    }

    private void AdvanceEpoch()
    {
        if (_binding.Epoch == ulong.MaxValue) throw Error(StatusCode.ResourceExhausted, "session epoch exhausted");
        _revoked.TrySetResult();
        _revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _binding = new SessionBinding(Guid.NewGuid(), _binding.Epoch + 1);
    }

    private int FindAncestor(ByteString hash)
    {
        if (hash == _anchor.AnchorHash) return -1;
        for (int i = 0; i < _blocks.Count; i++) if (_blocks[i].Data.Hash == hash) return i;
        return -2;
    }

    private int FindBlock(ulong number)
    {
        if (number <= _anchor.AnchorNumber || number > TipNumber) return -1;
        return checked((int)(number - _anchor.AnchorNumber - 1));
    }

    private void RemoveSuffix(int first)
    {
        for (int i = first; i < _blocks.Count; i++) Release(_blocks[i]);
        _blocks.RemoveRange(first, _blocks.Count - first);
    }

    private void Release(RetainedBlock block)
    {
        _bytes -= block.Size;
        _witnessItems -= block.WitnessItems;
    }

    private ServerFrame Frame(ulong requestId, Ready ready) => new() { SessionId = Id, RequestId = requestId, Epoch = _binding.Epoch, Ready = ready };
    private ulong TipNumber => _blocks.Count == 0 ? _anchor.AnchorNumber : _blocks[^1].Data.Number;
    private ByteString TipHash => _blocks.Count == 0 ? _anchor.AnchorHash : _blocks[^1].Data.Hash;
    private ByteString TipStateRoot => _blocks.Count == 0 ? _anchor.AnchorStateRoot : _blocks[^1].PostStateRoot;
    private ByteString PreStateRoot(int index) => index == 0 ? _anchor.AnchorStateRoot : _blocks[index - 1].PostStateRoot;
    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));
    private sealed record RetainedBlock(BlockWitness Data, ByteString PostStateRoot, long Size, long WitnessItems);
}

internal readonly record struct SessionBinding(Guid Attachment, ulong Epoch);
internal sealed record ExecutedStreamingBlock(ulong Number, ByteString Hash, ByteString ParentHash, ByteString PreStateRoot, ByteString PostStateRoot);
internal sealed record StreamingSessionLimits(int MaxBlocks, long MaxBytes, long MaxWitnessItems)
{
    internal void Validate()
    {
        if (MaxBlocks <= 0 || MaxBytes <= 0 || MaxWitnessItems <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBlocks));
    }
}
