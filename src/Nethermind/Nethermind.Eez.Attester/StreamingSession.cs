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
    private readonly StreamingProtocolRules _rules;
    private readonly NativeStreamingProfile? _nativeProfile;
    private NativeStreamingEvidence? _nativeHistory;
    private int _nativeHistoryLength;
    private int _anchorHistoryLength;
    private bool _attached = true;
    private readonly ulong _initialAnchorNumber;
    private ulong _provedThrough;
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

    internal StreamingSession(string authenticatedOwner, Begin begin, StreamingSessionLimits limits, StreamingProtocolRules rules = default, int frameBytes = 0, NativeStreamingProfile? nativeProfile = null)
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
        _initialAnchorNumber = begin.AnchorNumber;
        _provedThrough = begin.AnchorNumber;
        _anchor = new Begin
        {
            RollupId = begin.RollupId,
            AnchorNumber = begin.AnchorNumber,
            AnchorHash = begin.AnchorHash,
            AnchorStateRoot = begin.AnchorStateRoot,
        };
        _rules = rules;
        if (rules.HasNativeHistory != (nativeProfile is not null) || nativeProfile is not null && rules.Profile != StreamingSettlementProfile.RollupXNative) throw new ArgumentException("Native session profile does not match its execution configuration.", nameof(nativeProfile));
        _nativeProfile = nativeProfile;
        _nativeHistory = nativeProfile is null ? null : new NativeStreamingEvidence(limits.MaxWitnessItems);
        if (rules.ChargesWireFrames && (frameBytes <= 0 || frameBytes > limits.MaxBytes))
            throw Error(StatusCode.ResourceExhausted, "session anchor limit exceeded");
        _bytes = rules.ChargesWireFrames ? frameBytes : 0;
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

    internal bool CanEvict
    {
        get { lock (_lock) return !_attached && !_cancelled && _pending is null; }
    }

    internal bool Detach(SessionBinding binding)
    {
        lock (_lock)
        {
            if (_binding != binding) return false;
            _attached = false;
            return true;
        }
    }

    internal bool RetireDetached()
    {
        lock (_lock)
        {
            if (!CanEvict) return false;
            Retire();
            return true;
        }
    }

    internal void Retire()
    {
        lock (_lock)
        {
            _cancelled = true;
            _revoked.TrySetResult();
            RemoveSuffix(0);
            _nativeHistory = null;
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

    internal SessionBinding Resume(string authenticatedOwner) => ResumeAttachment(authenticatedOwner).Binding;

    internal (SessionBinding Binding, Task Revoked) ResumeAttachment(string authenticatedOwner)
    {
        lock (_lock)
        {
            if (_cancelled || !StringComparer.Ordinal.Equals(_owner, authenticatedOwner))
                throw Error(StatusCode.NotFound, "session unavailable");
            AdvanceEpoch();
            _attached = true;
            return (_binding, _revoked.Task);
        }
    }

    internal ServerFrame Rewind(SessionBinding binding, ulong requestId, ByteString ancestorHash) =>
        RewindAttachment(binding, requestId, ancestorHash).Response;

    internal (ServerFrame Response, SessionBinding Binding, Task Revoked) RewindAttachment(SessionBinding binding, ulong requestId, ByteString ancestorHash)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            if (ancestorHash.Length != 32) throw Error(_rules.InvalidAncestor, "ancestor hash must be 32 bytes");
            int index = FindAncestor(ancestorHash);
            if (index == -2) throw Error(StatusCode.FailedPrecondition, "ancestor outside validated prefix");
            AdvanceEpoch();
            RemoveSuffix(index + 1);
            if (_rules.HasNativeHistory) _nativeHistoryLength = _blocks.Count == 0 ? _anchorHistoryLength : _blocks[^1].NativeHistoryLength;
            _provedThrough = Math.Min(_provedThrough, TipNumber);
            return (Frame(requestId, new Ready { ValidatedThrough = TipNumber, ValidatedHash = TipHash }), _binding, _revoked.Task);
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
            _nativeHistory = null;
            return response;
        }
    }

    internal SessionOperation AdmitBlock(SessionBinding binding, ulong requestId, BlockWitness block, int frameBytes = 0)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            CheckIdle();
            if (block.Witness is null || block.Hash.Length != 32 || block.ParentHash.Length != 32)
                throw Error(StatusCode.InvalidArgument, "invalid block witness");
            if (!_rules.HasNativeHistory) WindowAssembler.RejectL1Evidence(block.Witness);
            if (block.Rlp.IsEmpty) throw Error(StatusCode.FailedPrecondition, "consensus block encoding is empty");
            int duplicate = FindBlock(block.Number);
            if (duplicate >= 0)
            {
                BlockWitness retained = _blocks[duplicate].Data;
                if (retained.Hash != block.Hash)
                    throw Error(StatusCode.FailedPrecondition, "different block at validated height");
                if (retained.ParentHash != block.ParentHash || retained.Rlp != block.Rlp)
                    throw Error(StatusCode.InvalidArgument, "duplicate metadata or encoding differs from validated block");
            }
            else if (TipNumber == ulong.MaxValue || block.Number != TipNumber + 1 || block.ParentHash != TipHash)
                throw Error(StatusCode.FailedPrecondition, "block outside next validated height");

            long size = _rules.ChargesWireFrames ? frameBytes : block.CalculateSize();
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(frameBytes));
            long items = (long)block.Witness.State.Count + block.Witness.Codes.Count + block.Witness.Keys.Count + block.Witness.Headers.Count + block.Witness.L1HeadersRlp.Count;
            if (_rules.RetainsProvedWindow)
            {
                int first = duplicate >= 0 ? _blocks.Count : checked((int)(_provedThrough - _anchor.AnchorNumber));
                CheckWindowQuota(first, _blocks.Count, size, items, 1);
            }
            else
            {
                long availableBytes = duplicate >= 0 ? _limits.MaxBytes : _limits.MaxBytes - _bytes;
                long availableItems = duplicate >= 0 ? _limits.MaxWitnessItems : _limits.MaxWitnessItems - _witnessItems;
                if (size > availableBytes || items > availableItems || (duplicate < 0 && _blocks.Count >= _limits.MaxBlocks))
                    throw Error(StatusCode.ResourceExhausted, "session block limit exceeded");
            }

            ByteString preStateRoot = duplicate >= 0 ? PreStateRoot(duplicate) : TipStateRoot;
            int nativeLength = _nativeHistoryLength;
            ExecutedStreamingBlock? reused = null;
            if (_rules.HasNativeHistory && duplicate >= 0)
            {
                RetainedBlock retained = _blocks[duplicate];
                if (!SameWitness(retained.Data.Witness, block.Witness))
                    throw Error(StatusCode.FailedPrecondition, "duplicate witness differs from checked execution");
                nativeLength = duplicate == 0 ? _anchorHistoryLength : _blocks[duplicate - 1].NativeHistoryLength;
                reused = new ExecutedStreamingBlock(block.Number, block.Hash, block.ParentHash, preStateRoot, retained.PostStateRoot);
            }
            _pending = new SessionOperation(this, binding, requestId, block.Clone(), preStateRoot, size, items, duplicate,
                _nativeProfile, _nativeHistory, nativeLength, reused);
            return _pending;
        }
    }

    internal SessionOperation AdmitFinalize(SessionBinding binding, ulong requestId, Finalize finalize, int frameBytes = 0)
    {
        lock (_lock)
        {
            CheckBinding(binding);
            CheckIdle();
            if (finalize.Settlement is null || finalize.TerminalHash.Length != 32 || finalize.FromBlock == 0 || finalize.FromBlock > finalize.ToBlock ||
                (!_rules.RetainsProvedWindow && finalize.FromBlock <= _initialAnchorNumber))
                throw Error(_rules.InvalidRange, "invalid finalization range or settlement");
            if (_rules.RetainsProvedWindow && finalize.FromBlock <= _anchor.AnchorNumber)
                throw Error(StatusCode.OutOfRange, "range starts before retained session prefix");
            int first = FindBlock(finalize.FromBlock);
            int last = FindBlock(finalize.ToBlock);
            if (first < 0 || last < first || _blocks[last].Data.Hash != finalize.TerminalHash)
                throw Error(StatusCode.FailedPrecondition, "range outside validated prefix");
            if (_rules.RetainsProvedWindow) CheckWindowQuota(first, last + 1);
            long settlementBytes = _rules.ChargesWireFrames ? frameBytes : finalize.CalculateSize();
            if (settlementBytes <= 0) throw new ArgumentOutOfRangeException(nameof(frameBytes));
            if (settlementBytes > (_rules.ChargesWireFrames ? _limits.MaxBytes - _bytes : _limits.MaxBytes))
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
            _pending = new SessionOperation(this, binding, requestId, finalize.Clone(), blocks, predecessor, _nativeProfile, _nativeHistory);
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
            if (operation.Duplicate < 0 && _rules.HasNativeHistory != (executed.NativeHistory is not null))
                throw Error(StatusCode.FailedPrecondition, "execution did not validate the session L1 evidence");
            if (operation.Duplicate >= 0)
            {
                if (executed.PostStateRoot != _blocks[operation.Duplicate].PostStateRoot)
                    throw Error(StatusCode.FailedPrecondition, "duplicate execution differs from validated block");
            }
            else
            {
                if (_rules.HasNativeHistory)
                {
                    NativeStreamingEvidence history = executed.NativeHistory!;
                    if (history.Count != operation.NativeHistoryLength + block.Witness.L1HeadersRlp.Count)
                        throw Error(StatusCode.FailedPrecondition, "executed L1 checkpoint differs from admitted evidence");
                }
                int historyLength = executed.NativeHistory?.Count ?? 0;
                _blocks.Add(new RetainedBlock(block, executed.PostStateRoot, operation.Size, operation.WitnessItems, historyLength));
                if (_rules.HasNativeHistory)
                {
                    _nativeHistory = executed.NativeHistory;
                    _nativeHistoryLength = historyLength;
                }
                if (!_rules.RetainsProvedWindow)
                {
                    _bytes += operation.Size;
                    _witnessItems += operation.WitnessItems;
                }
            }
            operation.Completed = true;
            return new ServerFrame
            {
                SessionId = Id,
                RequestId = operation.RequestId,
                Epoch = operation.Binding.Epoch,
                Validated = new Validated { Number = block.Number, Hash = block.Hash, PostStateRoot = executed.PostStateRoot, Reused = _rules.HasNativeHistory && operation.Duplicate >= 0 },
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

    internal (ServerFrame Response, SessionBinding Binding, Task Revoked) CompleteProofAndFence(SessionOperation operation, Proof proof)
    {
        lock (_lock)
        {
            CheckOperation(operation);
            (ulong from, ulong to) = operation.FinalizationRange;
            if (proof.PublicInputsHash.Length != 32 || proof.Proof_.IsEmpty)
                throw Error(StatusCode.Internal, "invalid proof backend result");
            int first = FindBlock(from);
            ServerFrame response = new() { SessionId = Id, RequestId = operation.RequestId, Epoch = operation.Binding.Epoch, Proof = proof.Clone() };
            AdvanceEpoch();
            _anchor = operation.Predecessor!;
            if (_rules.HasNativeHistory && first > 0) _anchorHistoryLength = _blocks[first - 1].NativeHistoryLength;
            if (!_rules.RetainsProvedWindow && !_rules.HasNativeHistory)
                for (int i = 0; i < first; i++) Release(_blocks[i]);
            _blocks.RemoveRange(0, first);
            if (_rules.RetainsProvedWindow) _provedThrough = Math.Max(_provedThrough, to);
            operation.Completed = true;
            return (response, _binding, _revoked.Task);
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

    private void CheckWindowQuota(int first, int end, long bytes = 0, long items = 0, int additionalBlocks = 0)
    {
        if ((long)end - first + additionalBlocks > _limits.MaxBlocks)
            throw Error(StatusCode.ResourceExhausted, "window block limit exceeded");
        if (bytes > _limits.MaxBytes || items > _limits.MaxWitnessItems)
            throw Error(StatusCode.ResourceExhausted, "window witness limit exceeded");
        for (int i = first; i < end; i++)
        {
            RetainedBlock block = _blocks[i];
            if (block.Size > _limits.MaxBytes - bytes || block.WitnessItems > _limits.MaxWitnessItems - items)
                throw Error(StatusCode.ResourceExhausted, "window witness limit exceeded");
            bytes += block.Size;
            items += block.WitnessItems;
        }
    }

    private void RemoveSuffix(int first)
    {
        if (!_rules.RetainsProvedWindow)
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
    private static bool SameWitness(ExecutionWitness left, ExecutionWitness right) =>
        left.State.Equals(right.State) && left.Codes.Equals(right.Codes) && left.Keys.Equals(right.Keys)
        && left.Headers.Equals(right.Headers) && left.L1HeadersRlp.Equals(right.L1HeadersRlp);

    private sealed record RetainedBlock(BlockWitness Data, ByteString PostStateRoot, long Size, long WitnessItems, int NativeHistoryLength);
}

internal readonly record struct SessionBinding(Guid Attachment, ulong Epoch);
internal sealed record ExecutedStreamingBlock(ulong Number, ByteString Hash, ByteString ParentHash, ByteString PreStateRoot, ByteString PostStateRoot, NativeStreamingEvidence? NativeHistory = null);
internal sealed record StreamingSessionLimits(int MaxBlocks, long MaxBytes, long MaxWitnessItems)
{
    internal void Validate()
    {
        if (MaxBlocks <= 0 || MaxBytes <= 0 || MaxWitnessItems <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBlocks));
    }
}
