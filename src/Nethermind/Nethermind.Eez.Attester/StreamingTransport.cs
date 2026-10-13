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
        bool inputClosed = false;
        try
        {
            read = Read(input, closed.Token);
            while (true)
            {
                Task revoked = connection.AttachmentRevoked;
                if (work is not null)
                {
                    Task incoming = inputClosed ? work.Response : read;
                    await Task.WhenAny(incoming, work.Response, revoked).WaitAsync(closed.Token).ConfigureAwait(false);
                    if (revoked.IsCompleted) throw Error(StatusCode.Aborted, "session attachment no longer current");
                    if (work.Response.IsCompleted)
                    {
                        if (!connection.CanComplete(work.Operation))
                        {
                            work.Dispose();
                            work = null;
                            continue;
                        }
                        ServerFrame response = await Complete(connection, work, closed.Token).ConfigureAwait(false);
                        work.Dispose();
                        work = null;
                        bool terminal = response.Proof is not null;
                        await Write(connection, output, response, closed.Token, terminal).ConfigureAwait(false);
                        if (terminal || inputClosed) return;
                        continue;
                    }
                }

                await Task.WhenAny(read, revoked).WaitAsync(closed.Token).ConfigureAwait(false);
                if (revoked.IsCompleted) throw Error(StatusCode.Aborted, "session attachment no longer current");
                if (!await read.ConfigureAwait(false))
                {
                    if (work is null) return;
                    inputClosed = true;
                    continue;
                }
                ClientFrame frame = input.Current;
                if (frame.CalculateSize() > _limits.MaxMessageBytes)
                    throw Error(StatusCode.ResourceExhausted, "stream message exceeds decoding limit");
                StreamingDispatch dispatch = connection.Dispatch(frame);
                if (dispatch.Response is { } reply)
                {
                    await Write(connection, output, reply, closed.Token, dispatch.EndStream).ConfigureAwait(false);
                    if (dispatch.EndStream) return;
                    if (work is not null && reply.Epoch != work.Operation.Binding.Epoch)
                    {
                        work.Dispose();
                        work = null;
                    }
                }
                else if (dispatch.Operation is { } operation)
                {
                    if (operation.Finalize is not null && _finalize is null)
                    {
                        ServerFrame rejected = RejectUnstarted(connection, operation, new Status(StatusCode.Unimplemented, "settlement authorization unavailable"));
                        await Write(connection, output, rejected, closed.Token).ConfigureAwait(false);
                    }
                    else if (_admission.TryAcquire() is not { } slot)
                    {
                        ServerFrame rejected = RejectUnstarted(connection, operation, new Status(StatusCode.ResourceExhausted, "another Prove request is already active"));
                        await Write(connection, output, rejected, closed.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        work = new StreamingWork(connection, operation, slot, Execute, _limits.OperationTimeout, _time, closed.Token);
                    }
                }

                read = Read(input, closed.Token);
            }
        }
        finally
        {
            connection.Detach();
            try { closed.Cancel(); }
            finally
            {
                try { work?.Dispose(); }
                finally
                {
                    if (read is not null) ObserveRead(read);
                    Interlocked.Decrement(ref _connections);
                }
            }
        }
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
                { Block: null, Proof: { } proof } => connection.CompleteProof(work.Operation, proof),
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

    private async Task<bool> Read(IAsyncStreamReader<ClientFrame> input, CancellationToken cancellation)
    {
        using CancellationTokenSource deadline = new(_limits.ReadTimeout, _time);
        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try { return await input.MoveNext(idle.Token).ConfigureAwait(false); }
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
