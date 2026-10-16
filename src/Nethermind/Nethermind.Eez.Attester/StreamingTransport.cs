// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Nethermind.Eez.Prove.V2;

namespace Nethermind.Eez.Attester;

internal sealed class StreamingTransport
{
    private readonly StreamingSessionRegistry _registry;
    private readonly BackendAdmission _admission;
    private readonly Func<SessionOperation, CancellationToken, Task<ExecutedStreamingBlock>> _execute;
    private readonly Func<SessionOperation, CancellationToken, Task<Proof>>? _finalize;
    private readonly StreamingTransportLimits _limits;
    private readonly ILogger<StreamingTransport> _logger;
    private readonly ulong _rollupId;
    private readonly TimeProvider _time;
    private int _connections;

    internal StreamingTransport(StreamingSessionRegistry registry, BackendAdmission admission, StreamingBlockExecutor executor,
        StreamingTransportLimits limits, ulong rollupId, ILogger<StreamingTransport> logger, TimeProvider? time = null,
        Func<SessionOperation, CancellationToken, Task<Proof>>? finalize = null)
        : this(registry, admission, (operation, cancellation) => Task.Run(() => executor.Execute(operation, cancellation), CancellationToken.None),
            limits, rollupId, logger, time, finalize)
    { }

    internal StreamingTransport(StreamingSessionRegistry registry, BackendAdmission admission,
        Func<SessionOperation, CancellationToken, Task<ExecutedStreamingBlock>> execute,
        StreamingTransportLimits limits, ulong rollupId, ILogger<StreamingTransport> logger, TimeProvider? time = null,
        Func<SessionOperation, CancellationToken, Task<Proof>>? finalize = null)
    {
        limits.Validate();
        if (rollupId == 0) throw new ArgumentOutOfRangeException(nameof(rollupId));
        _registry = registry;
        _admission = admission;
        _execute = execute;
        _finalize = finalize;
        _limits = limits;
        _rollupId = rollupId;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    internal async Task Run(IAsyncStreamReader<ClientFrame> input, IServerStreamWriter<ServerFrame> output,
        string authenticatedOwner, CancellationToken cancellation)
    {
        StreamingConnection connection = new(_registry, authenticatedOwner, _rollupId);
        if (Interlocked.Increment(ref _connections) > _limits.MaxConnections)
        {
            Interlocked.Decrement(ref _connections);
            throw Error(StatusCode.ResourceExhausted, "stream connection limit exceeded");
        }

        using CancellationTokenSource closed = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        StreamingWork? work = null;
        Task<bool>? read = null;
        ClientFrame? queued = null;
        CancellationTokenSource? queuedDeadline = null;
        bool inputClosed = false;
        CancellationTokenSource? retiredDeadline = null;
        CancellationTokenRegistration retirement = default;

        void BoundRetirement()
        {
            if (!connection.IsRetired || retiredDeadline is not null) return;
            retiredDeadline = new CancellationTokenSource(_limits.ReadTimeout, _time);
            retirement = retiredDeadline.Token.Register(static state => ((CancellationTokenSource)state!).Cancel(), closed);
        }

        try
        {
            read = Read(input, closed.Token);
            while (true)
            {
                Task revoked = connection.AttachmentRevoked;
                if (revoked.IsCompleted)
                {
                    work?.Dispose();
                    work = null;
                    connection.Retire();
                    BoundRetirement();
                    if (!await Retire(connection, input, output, read, queued, closed.Token).ConfigureAwait(false)) return;
                    read = null;
                    queued = null;
                    queuedDeadline?.Dispose();
                    queuedDeadline = null;
                    continue;
                }

                if (read?.IsCompleted == true)
                {
                    bool present = await read.ConfigureAwait(false);
                    read = null;
                    if (!present) inputClosed = true;
                    else
                    {
                        ClientFrame frame = input.Current;
                        CheckMessage(frame);
                        StreamingDispatch? dispatch;
                        if (frame.KindCase is ClientFrame.KindOneofCase.Block or ClientFrame.KindOneofCase.Finalize)
                        {
                            dispatch = connection.CheckDataFrame(frame);
                            if (dispatch is null)
                            {
                                queued = frame;
                                queuedDeadline = new CancellationTokenSource(_limits.OperationTimeout, _time);
                            }
                        }
                        else dispatch = connection.Dispatch(frame);
                        if (dispatch?.Response is { } reply)
                        {
                            BoundRetirement();
                            if (work is not null && (connection.IsRetired || reply.Ready is not null && reply.Epoch != work.Operation.Binding.Epoch))
                            {
                                work.Dispose();
                                work = null;
                            }
                            await Write(connection, output, reply, closed.Token, dispatch.EndStream).ConfigureAwait(false);
                            if (dispatch.EndStream) return;
                        }
                    }
                    continue;
                }

                if (work?.Response.IsCompleted == true)
                {
                    ServerFrame response;
                    try
                    {
                        if (connection.IsRetired || !connection.CanComplete(work.Operation))
                        {
                            work.Dispose();
                            work = null;
                            continue;
                        }
                        response = await Complete(connection, work, closed.Token).ConfigureAwait(false);
                    }
                    catch (RpcException) when (connection.AttachmentRevoked.IsCompleted)
                    {
                        work?.Dispose();
                        work = null;
                        continue;
                    }
                    work.Dispose();
                    work = null;
                    bool terminal = response.Proof is not null;
                    await Write(connection, output, response, closed.Token, terminal).ConfigureAwait(false);
                    if (terminal) return;
                    continue;
                }

                if (work is null && queued is not null)
                {
                    BackendAdmission.BackendLease? slot = null;
                    SessionOperation? admitted = null;
                    try
                    {
                        StreamingDispatch? dispatch = connection.CheckDataFrame(queued);
                        if (dispatch is null && queued.Finalize is not null && _finalize is null)
                            dispatch = connection.RejectQueued(queued, new Status(StatusCode.Unimplemented, "settlement authorization unavailable"));
                        if (dispatch is null)
                        {
                            closed.Token.ThrowIfCancellationRequested();
                            queuedDeadline!.Token.ThrowIfCancellationRequested();
                            slot = _admission.TryAcquire();
                            if (slot is null)
                            {
                                using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(closed.Token, queuedDeadline.Token);
                                slot = await Acquire(connection.AttachmentRevoked, waiting.Token).ConfigureAwait(false);
                            }
                            closed.Token.ThrowIfCancellationRequested();
                            queuedDeadline.Token.ThrowIfCancellationRequested();
                            dispatch = connection.Dispatch(queued);
                        }
                        if (dispatch.Operation is { } operation)
                        {
                            admitted = operation;
                            closed.Token.ThrowIfCancellationRequested();
                            queuedDeadline!.Token.ThrowIfCancellationRequested();
                            work = new StreamingWork(connection, operation, slot!, Execute, _limits.OperationTimeout, _time, closed.Token, queuedDeadline);
                            admitted = null;
                            slot = null;
                            queuedDeadline = null;
                        }
                        else
                        {
                            slot?.Dispose();
                            slot = null;
                            BoundRetirement();
                            await Write(connection, output, dispatch.Response!, closed.Token, dispatch.EndStream).ConfigureAwait(false);
                            if (dispatch.EndStream) return;
                        }
                    }
                    catch (OperationCanceledException) when (!closed.IsCancellationRequested && queuedDeadline!.IsCancellationRequested)
                    {
                        if (admitted is not null)
                        {
                            connection.FinishOperation(admitted);
                            admitted = null;
                        }
                        slot?.Dispose();
                        slot = null;
                        StreamingDispatch rejection = connection.RejectQueued(queued, new Status(StatusCode.DeadlineExceeded, "queued operation deadline exceeded"));
                        BoundRetirement();
                        await Write(connection, output, rejection.Response!, closed.Token, rejection.EndStream).ConfigureAwait(false);
                        if (rejection.EndStream) return;
                    }
                    catch (RpcException) when (connection.AttachmentRevoked.IsCompleted)
                    {
                        connection.Retire();
                        BoundRetirement();
                        if (!await Retire(connection, input, output, read, queued, closed.Token).ConfigureAwait(false)) return;
                        read = null;
                    }
                    finally
                    {
                        if (admitted is not null) connection.FinishOperation(admitted);
                        slot?.Dispose();
                        queuedDeadline?.Dispose();
                        queuedDeadline = null;
                        queued = null;
                    }
                    continue;
                }

                if (inputClosed && work is null) return;
                if (read is null && !inputClosed && queued is null) read = Read(input, closed.Token);
                if (read is not null && work is not null)
                    await Task.WhenAny(read, work.Response, revoked).WaitAsync(closed.Token).ConfigureAwait(false);
                else
                    await Task.WhenAny((Task?)read ?? work!.Response, revoked).WaitAsync(closed.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (retiredDeadline?.IsCancellationRequested == true && !cancellation.IsCancellationRequested)
        {
            throw Error(StatusCode.DeadlineExceeded, "retired stream deadline exceeded");
        }
        finally
        {
            retirement.Dispose();
            retiredDeadline?.Dispose();
            connection.Detach();
            try { closed.Cancel(); }
            finally
            {
                try { work?.Dispose(); }
                finally
                {
                    queuedDeadline?.Dispose();
                    if (read is not null) ObserveRead(read);
                    Interlocked.Decrement(ref _connections);
                }
            }
        }
    }

    private async ValueTask<BackendAdmission.BackendLease> Acquire(Task revoked, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (revoked.IsCompleted) throw Error(StatusCode.Aborted, "session attachment retired");
            if (_admission.TryAcquire() is { } slot) return slot;
            await Task.WhenAny(_admission.WaitUntilIdle(), revoked).WaitAsync(cancellation).ConfigureAwait(false);
        }
    }

    private async Task<bool> Retire(StreamingConnection connection, IAsyncStreamReader<ClientFrame> input,
        IServerStreamWriter<ServerFrame> output, Task<bool>? read, ClientFrame? queued, CancellationToken cancellation)
    {
        if (queued is null)
        {
            if (!await (read ?? Read(input, cancellation)).ConfigureAwait(false)) return false;
            queued = input.Current;
            CheckMessage(queued);
        }
        StreamingDispatch dispatch = connection.Retire(queued);
        await Write(connection, output, dispatch.Response!, cancellation).ConfigureAwait(false);
        return true;
    }

    private void CheckMessage(ClientFrame frame)
    {
        if (frame.CalculateSize() > _limits.MaxMessageBytes)
            throw Error(_registry.Rules.MessageSize, "stream message exceeds decoding limit");
    }

    internal static ServerFrame RejectUnstarted(StreamingConnection connection, SessionOperation operation, Status status)
    {
        try { return connection.RejectOperation(operation, status); }
        finally { connection.FinishOperation(operation); }
    }

    private async Task<StreamingResult> Execute(SessionOperation operation, CancellationToken cancellation) =>
        operation.Finalize is null
            ? new StreamingResult(await _execute(operation, cancellation).ConfigureAwait(false), null)
            : new StreamingResult(null, await _finalize!(operation, cancellation).ConfigureAwait(false));

    private async Task<ServerFrame> Complete(StreamingConnection connection, StreamingWork work, CancellationToken cancellation)
    {
        try
        {
            StreamingResult result = await work.Response.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            work.CheckCancellation();
            return result switch
            {
                { Block: { } block, Proof: null } => connection.CommitBlock(work.Operation, block),
                { Block: null, Proof: { } proof } => CompleteProof(connection, work.Operation, proof),
                _ => throw new InvalidOperationException("invalid streaming backend result"),
            };
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && work.DeadlineExceeded)
        {
            return connection.RejectOperation(work.Operation, new Status(StatusCode.DeadlineExceeded, "stream operation deadline exceeded"));
        }
        catch (RpcException exception)
        {
            return connection.RejectOperation(work.Operation, exception.Status,
                exception.Trailers.GetValueBytes("grpc-status-details-bin") is { } details ? ByteString.CopyFrom(details) : null);
        }
        catch (Exception exception) when (exception is not RpcException and not OperationCanceledException)
        {
            _logger.LogError(exception, "stream execution worker failed");
            return connection.RejectOperation(work.Operation, new Status(StatusCode.Internal, "stream execution worker failed"));
        }
    }

    private ServerFrame CompleteProof(StreamingConnection connection, SessionOperation operation, Proof proof)
    {
        ServerFrame response = connection.CompleteProof(operation, proof);
        Finalize finalization = operation.Finalize!;
        _logger.LogInformation(
            "{{\"event_name\":\"eez.proof_signer.window_signed\",\"fields\":{{" +
            "\"wire\":\"prove.v2\",\"validated_from_block\":{From},\"validated_to_block\":{To}," +
            "\"recomputed_public_inputs_hash\":\"0x{Hash}\"}}}}",
            finalization.FromBlock, finalization.ToBlock, Convert.ToHexStringLower(proof.PublicInputsHash.Span));
        return response;
    }

    private async Task<bool> Read(IAsyncStreamReader<ClientFrame> input, CancellationToken cancellation)
    {
        using CancellationTokenSource deadline = new(_limits.ReadTimeout, _time);
        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try { return await input.MoveNext(idle.Token).ConfigureAwait(false); }
        catch (RpcException exception) when (_registry.Rules.MessageSize == StatusCode.OutOfRange &&
            exception.StatusCode == StatusCode.ResourceExhausted &&
            exception.Status.Detail == "Received message exceeds the maximum configured message size.")
        {
            throw Error(StatusCode.OutOfRange, exception.Status.Detail);
        }
        catch (Exception exception) when (exception is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled } && !cancellation.IsCancellationRequested)
        {
            throw Error(StatusCode.DeadlineExceeded, "stream idle timeout");
        }
    }

    private async Task Write(StreamingConnection connection, IServerStreamWriter<ServerFrame> output,
        ServerFrame response, CancellationToken cancellation, bool terminal = false)
    {
        if (!terminal) connection.CheckResponse(response);
        Task? revoked = response.Proof is not null ? connection.ProofRevoked : terminal ? null : connection.AttachmentRevoked;
        if (revoked?.IsCompleted == true) throw Error(StatusCode.Aborted, "session attachment no longer current");
        using CancellationTokenSource timeout = new(_limits.WriteTimeout, _time);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        Task write = output.WriteAsync(response, deadline.Token);
        try
        {
            if (revoked is not null)
            {
                await Task.WhenAny(write, revoked).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (revoked.IsCompleted) throw Error(StatusCode.Aborted, "session attachment no longer current");
            }
            await write.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw Error(StatusCode.DeadlineExceeded, "stream write deadline exceeded");
        }
        finally
        {
            deadline.Cancel();
            _ = write.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static void ObserveRead(Task<bool> read) => _ = read.ContinueWith(static failed => _ = failed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));
}

internal sealed record StreamingTransportLimits(int MaxConnections, int MaxMessageBytes, TimeSpan ReadTimeout,
    TimeSpan WriteTimeout, TimeSpan OperationTimeout)
{
    internal void Validate()
    {
        if (MaxConnections <= 0 || MaxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxConnections));
        ValidateTimeout(ReadTimeout);
        ValidateTimeout(WriteTimeout);
        ValidateTimeout(OperationTimeout);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }
}
