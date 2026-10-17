// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Nethermind.Eez.Prove;

namespace Nethermind.Eez.Attester;

/// <summary>
/// The <c>prove.v1.Prover</c> service: one request at a time, each streamed window admitted within the quotas and
/// timeouts, then attested by the pipeline. A request that outlives its deadline keeps the slot until its work
/// reaches a checkpoint, so a retry cannot run concurrently with it.
/// </summary>
internal sealed class ProverService : Prover.ProverBase
{
    private const string DeadlineExceeded = "Prove request deadline exceeded";
    private readonly ulong _rollupId;
    private readonly NativeProver? _native;
    private readonly Func<AdmittedWindow, CancellationToken, Task<ProveResponse>> _execute;
    private readonly AttesterOptions _options;
    private readonly BackendAdmission _admission;
    private readonly ILogger<ProverService> _logger;

    public ProverService(AttestationPipeline pipeline, AttesterOptions options, BackendAdmission admission, ILogger<ProverService> logger)
        : this(pipeline.Context.RollupId, (window, cancellation) => Task.Run(() => pipeline.Run(window, cancellation), CancellationToken.None),
            options, admission, logger)
    { }

    internal ProverService(ulong rollupId, Func<AdmittedWindow, CancellationToken, Task<ProveResponse>> execute,
        AttesterOptions options, BackendAdmission admission, ILogger<ProverService> logger, NativeProver? native = null)
    {
        _native = native;
        _rollupId = rollupId;
        _execute = execute;
        _options = options;
        _admission = admission;
        _logger = logger;
    }

    public override Task<CapabilitiesResponse> Capabilities(CapabilitiesRequest request, ServerCallContext context) =>
        _native is null ? base.Capabilities(request, context) : Task.FromResult(_native.Capabilities(_options));

    public override async Task<ProveResponse> Prove(IAsyncStreamReader<ProveChunk> requestStream, ServerCallContext context)
    {
        BackendAdmission.BackendLease? slot = _admission.TryAcquire();
        if (slot is null)
        {
            _logger.LogWarning("Prove request rejected: another request is active");
            throw new RpcException(new Status(StatusCode.Unavailable, "another Prove request is already active"));
        }

        bool slotHandedOff = false;
        long started = Environment.TickCount64;
        try
        {
            using CancellationTokenSource deadline = new(_options.RequestTimeout);
            AdmittedWindow window = await ReadWindow(requestStream, context.CancellationToken, deadline.Token);
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadline.Token);
            CancellationToken workerCancellation = cancellation.Token;
            Task<ProveResponse> work;
            try { work = _execute(window, workerCancellation); }
            catch (Exception exception) { work = Task.FromException<ProveResponse>(exception); }
            slotHandedOff = true;
            _ = slot.ReleaseAfter(work, () =>
            {
                window.Dispose();
                cancellation.Dispose();
            });

            ProveResponse response;
            try
            {
                response = await work.WaitAsync(workerCancellation);
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
                _logger.LogError(e, "request pipeline worker failed");
                throw new RpcException(new Status(StatusCode.Internal, "request pipeline worker failed"));
            }

            _logger.LogInformation(
                "{{\"event_name\":\"eez.proof_signer.window_signed\",\"fields\":{{" +
                "\"validated_from_block\":{From},\"validated_to_block\":{To}," +
                "\"recomputed_public_inputs_hash\":\"0x{Hash}\",\"elapsed_ms\":{Elapsed}}}}}",
                window.FromBlock, window.ToBlock, Convert.ToHexStringLower(response.PublicInputsHash.Span), Environment.TickCount64 - started);
            return response;
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Prove request cancelled by the composer after {Elapsed} ms", Environment.TickCount64 - started);
            throw;
        }
        catch (RpcException e)
        {
            _logger.LogWarning("Prove request failed: {Code} {Message} after {Elapsed} ms", e.StatusCode, e.Status.Detail, Environment.TickCount64 - started);
            throw;
        }
        finally
        {
            if (!slotHandedOff)
            {
                slot.Dispose();
            }
        }
    }

    /// <summary>Waits until no request holds the slot, so shutdown lets a running attestation finish.</summary>
    public Task WaitUntilIdle() => _admission.WaitUntilIdle();

    private async Task<AdmittedWindow> ReadWindow(IAsyncStreamReader<ProveChunk> stream, CancellationToken aborted, CancellationToken deadline)
    {
        ProveChunk first = await Next(stream, aborted, deadline) ?? throw WindowAssembler.Invalid("empty Prove stream");
        WindowAssembler assembler = WindowAssembler.Start(_options.Limits, first, _native?.Identity);
        if (assembler.RollupId != _rollupId)
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
        idle.CancelAfter(_options.StreamIdleTimeout);
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
