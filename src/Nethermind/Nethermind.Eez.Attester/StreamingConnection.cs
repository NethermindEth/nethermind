// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingConnection(StreamingSessionRegistry registry, string authenticatedOwner, ulong rollupId)
{
    private static readonly Task Unbound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    private readonly object _lock = new();
    private readonly string _owner = ValidateOwner(authenticatedOwner);
    private readonly ulong _rollupId = rollupId != 0 ? rollupId : throw new ArgumentOutOfRangeException(nameof(rollupId));
    private StreamingSession? _session;
    private SessionBinding _binding;
    private SessionOperation? _pending;
    private bool _ended;
    private bool _retired;
    private Task? _proofRevoked;
    private Task _attachmentRevoked = Unbound;

    private void ValidateSettlement(SettlementInput? settlement)
    {
        switch (registry.Rules.Profile)
        {
            case StreamingSettlementProfile.Rollup0Calldata:
            case StreamingSettlementProfile.Rollup0Conditional: StreamingFinalizer.ValidateSupportedSettlement(settlement); break;
            case StreamingSettlementProfile.RollupXNative: RollupXStreamingFinalizer.ValidateSupportedSettlement(settlement); break;
            default: throw new InvalidOperationException("unknown settlement profile");
        }
    }

    internal StreamingDispatch Dispatch(ClientFrame frame)
    {
        lock (_lock)
        {
            if (_ended) throw new InvalidOperationException("stream ended");
            try
            {
                if (_session is null) return Bind(frame);
                StreamingDispatch? rejection = AuthorizeBound(frame);
                if (rejection is not null) return rejection;
                return DispatchBound(frame);
            }
            catch (RpcException exception)
            {
                if (_session is null) return StreamingDispatch.Reply(RejectUnbound(frame.RequestId, exception.Status));
                if (_attachmentRevoked.IsCompleted) return Retire(frame);
                try { return StreamingDispatch.Reply(Reject(frame.RequestId, exception.Status)); }
                catch (RpcException) when (_attachmentRevoked.IsCompleted) { return Retire(frame); }
            }
        }
    }

    private StreamingDispatch? AuthorizeBound(ClientFrame frame)
    {
        if (_retired || _attachmentRevoked.IsCompleted) return Retire(frame);
        if (frame.KindCase is ClientFrame.KindOneofCase.Begin or ClientFrame.KindOneofCase.Resume)
        {
            Status status = new(StatusCode.InvalidArgument, "stream already bound");
            if (registry.Rules.ReturnsBoundControlErrors && frame.SessionId == _session!.Id && frame.Epoch == _binding.Epoch)
                return StreamingDispatch.Reply(Reject(frame.RequestId, status));
            return StreamingDispatch.Reply(RejectUnbound(frame.RequestId, status));
        }
        if (frame.SessionId.Length != 32)
            return StreamingDispatch.Reply(RejectUnbound(frame.RequestId, new Status(StatusCode.InvalidArgument, "session id must be 32 bytes")));
        if (frame.SessionId != _session!.Id || frame.Epoch != _binding.Epoch)
            return StreamingDispatch.Reply(RejectUnbound(frame.RequestId, new Status(StatusCode.PermissionDenied, "frame outside attachment")));
        registry.Touch(_session, _binding);
        if (_pending is not null && _pending.RequestId == frame.RequestId)
            return StreamingDispatch.Reply(Reject(frame.RequestId, new Status(StatusCode.InvalidArgument, "request id already outstanding")));
        return null;
    }

    internal StreamingDispatch? CheckDataFrame(ClientFrame frame)
    {
        lock (_lock)
        {
            if (_ended) throw new InvalidOperationException("stream ended");
            if (_session is null) return Dispatch(frame);
            try
            {
                StreamingDispatch? rejection = AuthorizeBound(frame);
                if (rejection is not null) return rejection;
                if (frame.Finalize is not null) ValidateSettlement(frame.Finalize.Settlement);
                return null;
            }
            catch (RpcException exception)
            {
                if (_attachmentRevoked.IsCompleted) return Retire(frame);
                try { return StreamingDispatch.Reply(Reject(frame.RequestId, exception.Status)); }
                catch (RpcException) when (_attachmentRevoked.IsCompleted) { return Retire(frame); }
            }
        }
    }

    internal StreamingDispatch RejectQueued(ClientFrame frame, Status status)
    {
        lock (_lock)
        {
            StreamingDispatch? rejection = CheckDataFrame(frame);
            if (rejection is not null) return rejection;
            try { return StreamingDispatch.Reply(Reject(frame.RequestId, status)); }
            catch (RpcException) when (_attachmentRevoked.IsCompleted) { return Retire(frame); }
        }
    }

    private StreamingDispatch Bind(ClientFrame frame)
    {
        if (frame.Epoch != 0) throw Error(StatusCode.InvalidArgument, "unbound stream requires epoch zero");
        switch (frame.KindCase)
        {
            case ClientFrame.KindOneofCase.Begin:
                if (!frame.SessionId.IsEmpty) throw Error(StatusCode.InvalidArgument, "Begin requires empty session id");
                if (frame.Begin.RollupId == 0) throw Error(registry.Rules.ZeroRollup, "rollup identity missing");
                if (frame.Begin.RollupId != _rollupId) throw Error(StatusCode.Unimplemented, "rollup identity unsupported");
                _session = registry.Begin(_owner, frame.Begin, WireSize(frame));
                _binding = _session.Binding;
                _attachmentRevoked = _session.AttachmentRevoked(_binding);
                return StreamingDispatch.Reply(_session.Ready(_binding, frame.RequestId));
            case ClientFrame.KindOneofCase.Resume:
                if (frame.SessionId.Length != 32) throw Error(StatusCode.InvalidArgument, "Resume requires a session id");
                (_session, _binding, _attachmentRevoked) = registry.ResumeAttachment(_owner, frame.SessionId, _rollupId);
                return StreamingDispatch.Reply(_session.Ready(_binding, frame.RequestId));
            case ClientFrame.KindOneofCase.None:
                if (!frame.SessionId.IsEmpty) throw Error(frame.SessionId.Length == 32 ? StatusCode.PermissionDenied : StatusCode.InvalidArgument, "unbound session id");
                throw Error(StatusCode.Unimplemented, "unsupported operation");
            default:
                if (!frame.SessionId.IsEmpty) throw Error(frame.SessionId.Length == 32 ? StatusCode.PermissionDenied : StatusCode.InvalidArgument, "unbound session id");
                throw Error(registry.Rules.UnboundControl, "Begin or Resume must be first");
        }
    }

    private StreamingDispatch DispatchBound(ClientFrame frame)
    {
        switch (frame.KindCase)
        {
            case ClientFrame.KindOneofCase.Block:
                if (frame.Block.Data is null) throw Error(StatusCode.InvalidArgument, "block data missing");
                _pending = _session!.AdmitBlock(_binding, frame.RequestId, frame.Block.Data, WireSize(frame));
                return StreamingDispatch.Work(_pending);
            case ClientFrame.KindOneofCase.Finalize:
                ValidateSettlement(frame.Finalize.Settlement);
                _pending = _session!.AdmitFinalize(_binding, frame.RequestId, frame.Finalize, WireSize(frame));
                return StreamingDispatch.Work(_pending);
            case ClientFrame.KindOneofCase.Rewind:
                (ServerFrame response, SessionBinding binding, Task revoked) = _session!.RewindAttachment(_binding, frame.RequestId, frame.Rewind.AncestorHash);
                _binding = binding;
                _attachmentRevoked = revoked;
                return StreamingDispatch.Reply(response);
            case ClientFrame.KindOneofCase.Cancel:
                ServerFrame cancelled = _session!.Cancel(_binding, frame.RequestId);
                _ended = true;
                return StreamingDispatch.End(cancelled);
            case ClientFrame.KindOneofCase.None:
                throw Error(StatusCode.Unimplemented, "unsupported operation");
            default:
                throw Error(StatusCode.FailedPrecondition, "stream already bound");
        }
    }

    internal ServerFrame CommitBlock(SessionOperation operation, ExecutedStreamingBlock executed)
    {
        lock (_lock)
        {
            CheckPending(operation);
            return _session!.CommitBlock(operation, executed);
        }
    }

    internal ServerFrame CompleteProof(SessionOperation operation, Proof proof)
    {
        lock (_lock)
        {
            CheckPending(operation);
            (ServerFrame response, SessionBinding binding, Task revoked) = _session!.CompleteProofAndFence(operation, proof);
            _binding = binding;
            _proofRevoked = revoked;
            _ended = true;
            return response;
        }
    }

    internal ServerFrame RejectOperation(SessionOperation operation, Status status, ByteString? details = null)
    {
        lock (_lock)
        {
            CheckPending(operation);
            return Reject(operation.RequestId, status, details);
        }
    }

    internal Task ProofRevoked
    {
        get { lock (_lock) return _proofRevoked ?? throw new InvalidOperationException("proof completion fence missing"); }
    }

    internal Task AttachmentRevoked
    {
        get { lock (_lock) return _retired ? Unbound : _attachmentRevoked; }
    }

    internal bool IsRetired
    {
        get { lock (_lock) return _retired; }
    }

    internal void Retire()
    {
        lock (_lock) _retired = true;
    }

    internal bool CanComplete(SessionOperation operation)
    {
        lock (_lock)
        {
            if (_ended || _retired) throw Error(StatusCode.Aborted, "stream ended");
            _session!.CheckCurrent(_binding);
            return ReferenceEquals(_pending, operation) && operation.Binding == _binding;
        }
    }

    internal void CheckResponse(ServerFrame response)
    {
        lock (_lock)
        {
            if (_ended) throw Error(StatusCode.Aborted, "stream ended");
            if (_retired && response.SessionId.IsEmpty && response.Epoch == 0 &&
                response.Rejected is { ValidatedThrough: 0 } retired && retired.ValidatedHash.IsEmpty) return;
            if (_session is not null)
            {
                _session.CheckCurrent(_binding);
                if (response.SessionId.IsEmpty && response.Epoch == 0 && response.Rejected is { ValidatedThrough: 0 } rejected && rejected.ValidatedHash.IsEmpty) return;
                if (response.SessionId != _session.Id || response.Epoch != _binding.Epoch)
                    throw Error(StatusCode.Aborted, "response outside current attachment");
            }
        }
    }

    internal StreamingDispatch Retire(ClientFrame frame)
    {
        lock (_lock)
        {
            _retired = true;
            StatusCode code = frame.SessionId.Length != 32 ? StatusCode.InvalidArgument :
                frame.SessionId == _session?.Id && (frame.Epoch == _binding.Epoch || frame.Resume is not null && frame.Epoch == 0)
                    ? registry.Rules.RetiredAttachment : StatusCode.PermissionDenied;
            return StreamingDispatch.Reply(RejectUnbound(frame.RequestId, new Status(code, "session attachment retired")));
        }
    }

    private static ServerFrame RejectUnbound(ulong requestId, Status status) => new()
    {
        RequestId = requestId,
        Rejected = new Rejected { Code = (int)status.StatusCode, Message = status.Detail },
    };

    internal void Detach()
    {
        lock (_lock)
        {
            _ended = true;
            if (_session is not null) registry.Detach(_session, _binding);
        }
    }

    internal void FinishOperation(SessionOperation operation)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_pending, operation)) throw new InvalidOperationException("operation outside stream");
            operation.Dispose();
            _pending = null;
        }
    }

    private void CheckPending(SessionOperation operation)
    {
        if (_ended || _retired || !ReferenceEquals(_pending, operation)) throw Error(StatusCode.Aborted, "operation outside active stream");
        _session!.CheckCurrent(operation.Binding);
    }

    private ServerFrame Reject(ulong requestId, Status status, ByteString? details = null)
    {
        ServerFrame response = _session is null ? new ServerFrame { RequestId = requestId } : _session.Ready(_binding, requestId);
        ulong number = response.Ready?.ValidatedThrough ?? 0;
        ByteString hash = response.Ready?.ValidatedHash ?? ByteString.Empty;
        response.Rejected = new Rejected
        {
            Code = (int)status.StatusCode,
            Details = details ?? ByteString.Empty,
            Message = status.Detail,
            ValidatedThrough = number,
            ValidatedHash = hash,
        };
        return response;
    }

    private int WireSize(ClientFrame frame) => registry.Rules.ChargesWireFrames ? frame.CalculateSize() : 0;

    private static string ValidateOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (owner.Length > 256) throw new ArgumentOutOfRangeException(nameof(owner));
        return owner;
    }

    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));
}

internal sealed record StreamingDispatch
{
    internal ServerFrame? Response { get; }
    internal SessionOperation? Operation { get; }
    internal bool EndStream { get; }

    private StreamingDispatch(ServerFrame? response, SessionOperation? operation, bool endStream) =>
        (Response, Operation, EndStream) = (response, operation, endStream);

    internal static StreamingDispatch Reply(ServerFrame response) => new(response, null, false);
    internal static StreamingDispatch End(ServerFrame response) => new(response, null, true);
    internal static StreamingDispatch Work(SessionOperation operation) => new(null, operation, false);
}
