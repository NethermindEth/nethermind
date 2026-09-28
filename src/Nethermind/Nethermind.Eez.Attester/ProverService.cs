// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Nethermind.Eez.Attester.Rpc;

namespace Nethermind.Eez.Attester;

/// <summary>
/// The <c>prove.v1.Prover</c> service: one request at a time, each streamed window admitted within the quotas and
/// timeouts, then attested by the pipeline. A request that outlives its deadline keeps the slot until its work
/// reaches a checkpoint, so a retry cannot run concurrently with it.
/// </summary>
internal sealed class ProverService(AttestationPipeline pipeline, AttesterOptions options, ILogger<ProverService> logger) : Prover.ProverBase
{
    private const string DeadlineExceeded = "Prove request deadline exceeded";

    private readonly SemaphoreSlim _slot = new(1, 1);

    public override async Task<ProveResponse> Prove(IAsyncStreamReader<ProveChunk> requestStream, ServerCallContext context)
    {
        if (!_slot.Wait(0))
        {
            logger.LogWarning("Prove request rejected: another request is active");
            throw new RpcException(new Status(StatusCode.Unavailable, "another Prove request is already active"));
        }

        bool slotHandedOff = false;
        long started = Environment.TickCount64;
        try
        {
            using CancellationTokenSource deadline = new(options.RequestTimeout);
            AdmittedWindow window = await ReadWindow(requestStream, context.CancellationToken, deadline.Token);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadline.Token);
            Task<ProveResponse> work = Task.Run(() => pipeline.Run(window, cancellation.Token), CancellationToken.None);
            slotHandedOff = true;
            _ = work.ContinueWith(_ =>
            {
                window.Dispose();
                _slot.Release();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            ProveResponse response;
            try
            {
                response = await work.WaitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, DeadlineExceeded));
            }
            catch (OperationCanceledException)
            {
                throw new RpcException(new Status(StatusCode.Cancelled, "Prove request cancelled"));
            }
            catch (Exception e) when (e is not RpcException)
            {
                logger.LogError(e, "request pipeline worker failed");
                throw new RpcException(new Status(StatusCode.Internal, "request pipeline worker failed"));
            }

            logger.LogInformation("window validated and signed: blocks {From}..{To}, public inputs hash 0x{Hash}, {Elapsed} ms", window.FromBlock, window.ToBlock,
                Convert.ToHexStringLower(response.PublicInputsHash.Span), Environment.TickCount64 - started);
            return response;
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled)
        {
            logger.LogInformation("Prove request cancelled by the composer after {Elapsed} ms", Environment.TickCount64 - started);
            throw;
        }
        catch (RpcException e)
        {
            logger.LogWarning("Prove request failed: {Code} {Message} after {Elapsed} ms", e.StatusCode, e.Status.Detail, Environment.TickCount64 - started);
            throw;
        }
        finally
        {
            if (!slotHandedOff)
            {
                _slot.Release();
            }
        }
    }

    /// <summary>Waits until no request holds the slot, so shutdown lets a running attestation finish.</summary>
    public async Task WaitUntilIdle()
    {
        await _slot.WaitAsync();
        _slot.Release();
    }

    private async Task<AdmittedWindow> ReadWindow(IAsyncStreamReader<ProveChunk> stream, CancellationToken aborted, CancellationToken deadline)
    {
        ProveChunk first = await Next(stream, aborted, deadline) ?? throw WindowAssembler.Invalid("empty Prove stream");
        WindowAssembler assembler = WindowAssembler.Start(options.Limits, first);
        if (assembler.RollupId != pipeline.Context.RollupId)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "window rollup identity rejected"));
        }

        try
        {
            while (await Next(stream, aborted, deadline) is { } chunk)
            {
                assembler.Push(chunk);
            }

            return assembler.Finish();
        }
        catch
        {
            assembler.Abandon();
            throw;
        }
    }

    private async Task<ProveChunk?> Next(IAsyncStreamReader<ProveChunk> stream, CancellationToken aborted, CancellationToken deadline)
    {
        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(aborted, deadline);
        idle.CancelAfter(options.StreamIdleTimeout);
        try
        {
            return await stream.MoveNext(idle.Token) ? stream.Current : null;
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.ResourceExhausted)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Prove message exceeds decoding limit"));
        }
        catch (Exception e) when (e is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled } && !aborted.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, deadline.IsCancellationRequested ? DeadlineExceeded : "Prove stream idle timeout"));
        }
    }
}
