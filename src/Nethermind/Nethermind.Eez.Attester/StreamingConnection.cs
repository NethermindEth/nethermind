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
    private Task? _proofRevoked;

    internal StreamingDispatch Dispatch(ClientFrame frame)
    {
        lock (_lock)
        {
            if (_ended) throw new InvalidOperationException("stream ended");
            try
            {
                if (_session is null) return Bind(frame);
                registry.Touch(_session, _binding);
                if (frame.SessionId != _session.Id || frame.Epoch != _binding.Epoch)
                    throw Error(StatusCode.FailedPrecondition, "frame session or epoch differs from attachment");
                if (_pending is not null && _pending.RequestId == frame.RequestId)
                    throw Error(StatusCode.InvalidArgument, "request id already outstanding");
                return DispatchBound(frame);
            }
            catch (RpcException exception)
            {
                return StreamingDispatch.Reply(Reject(frame.RequestId, exception.Status));
            }
        }
    }

    private StreamingDispatch Bind(ClientFrame frame)
    {
        if (frame.Epoch != 0) throw Error(StatusCode.InvalidArgument, "unbound stream requires epoch zero");
        switch (frame.KindCase)
        {
            case ClientFrame.KindOneofCase.Begin:
                if (!frame.SessionId.IsEmpty) throw Error(StatusCode.InvalidArgument, "Begin requires empty session id");
                if (frame.Begin.RollupId != _rollupId) throw Error(StatusCode.Unimplemented, "rollup identity unsupported");
                _session = registry.Begin(_owner, frame.Begin);
                _binding = _session.Binding;
                return StreamingDispatch.Reply(_session.Ready(_binding, frame.RequestId));
            case ClientFrame.KindOneofCase.Resume:
                if (frame.SessionId.Length != 32) throw Error(StatusCode.InvalidArgument, "Resume requires a session id");
                (_session, _binding) = registry.Resume(_owner, frame.SessionId, _rollupId);
                return StreamingDispatch.Reply(_session.Ready(_binding, frame.RequestId));
            case ClientFrame.KindOneofCase.None:
                throw Error(StatusCode.Unimplemented, "unsupported operation");
            default:
                throw Error(StatusCode.InvalidArgument, "Begin or Resume must be first");
        }
    }

    private StreamingDispatch DispatchBound(ClientFrame frame)
    {
        switch (frame.KindCase)
        {
            case ClientFrame.KindOneofCase.Block:
                if (frame.Block.Data is null) throw Error(StatusCode.InvalidArgument, "block data missing");
                _pending = _session!.AdmitBlock(_binding, frame.RequestId, frame.Block.Data);
                return StreamingDispatch.Work(_pending);
            case ClientFrame.KindOneofCase.Finalize:
                _pending = _session!.AdmitFinalize(_binding, frame.RequestId, frame.Finalize);
                return StreamingDispatch.Work(_pending);
            case ClientFrame.KindOneofCase.Rewind:
                (ServerFrame response, SessionBinding binding) = _session!.RewindAttachment(_binding, frame.RequestId, frame.Rewind.AncestorHash);
                _binding = binding;
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
            (ServerFrame response, Task revoked) = _session!.CompleteProofAndFence(operation, proof);
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
        get { lock (_lock) return _session?.AttachmentRevoked(_binding) ?? Unbound; }
    }

    internal bool CanComplete(SessionOperation operation)
    {
        lock (_lock)
        {
            if (_ended) throw Error(StatusCode.Aborted, "stream ended");
            _session!.CheckCurrent(_binding);
            return ReferenceEquals(_pending, operation) && operation.Binding == _binding;
        }
    }

    internal void CheckResponse(ServerFrame response)
    {
        lock (_lock)
        {
            if (_ended) throw Error(StatusCode.Aborted, "stream ended");
            if (_session is not null)
            {
                _session.CheckCurrent(_binding);
                if (response.SessionId != _session.Id || response.Epoch != _binding.Epoch)
                    throw Error(StatusCode.Aborted, "response outside current attachment");
            }
        }
    }

    internal void Detach()
    {
        lock (_lock) _ended = true;
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
        if (_ended || !ReferenceEquals(_pending, operation)) throw Error(StatusCode.Aborted, "operation outside active stream");
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
