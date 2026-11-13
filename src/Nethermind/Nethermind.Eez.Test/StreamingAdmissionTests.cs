// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class StreamingAdmissionTests
{
    [Test]
    public async Task Prove_CancelledResponseWhileWorkerRuns_KeepsSharedAdmissionUntilDrain()
    {
        BackendAdmission admission = new(NullLogger<BackendAdmission>.Instance);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<ProveResponse> worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workerCancellation = default;
        AttesterOptions options = new(new IPEndPoint(IPAddress.Loopback, 0), "", 1, default, Address.Zero, Address.Zero, "", "", 2,
            30_000_000, WindowLimits.Default, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        ProverService prover = new(1, (_, token) => { workerCancellation = token; return worker.Task; }, options, admission, NullLogger<ProverService>.Instance);
        ByteString hash = ByteString.CopyFrom(new byte[32]);
        Reader input = new([
            new ProveChunk { Header = new ProveHeader { RollupId = 1, FromBlock = 1, ToBlock = 1, PostBatch = new PostBatch { AbiCalldata = hash } } },
            new ProveChunk { Block = new BlockWitness { Number = 1, Hash = hash, ParentHash = hash, Rlp = hash, Witness = new ExecutionWitness() } },
        ]);
        Task<ProveResponse> response = prover.Prove(input, new AttesterCallContext(cancellation.Token));
        Assert.That(workerCancellation.CanBeCanceled, Is.True, "precondition: input admission finished and the backend worker started");
        cancellation.Cancel();
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => response))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Cancelled), "response cancellation is reported before worker completion");
            Assert.That(admission.TryAcquire(), Is.Null, "a cancelled v1 response cannot admit a second expensive worker");
            Assert.That(prover.WaitUntilIdle().IsCompleted, Is.False, "shutdown waits for the real worker");
            Assert.That(workerCancellation.IsCancellationRequested, Is.True, "the v1 worker receives cancellation without losing its owned token source");
        }
        worker.SetResult(new ProveResponse());
        await prover.WaitUntilIdle();
        using BackendAdmission.BackendLease? retry = admission.TryAcquire();
        Assert.That(retry, Is.Not.Null, "actual worker completion restores shared admission");
    }

    private sealed class Reader(IEnumerable<ProveChunk> chunks) : IAsyncStreamReader<ProveChunk>
    {
        private readonly Queue<ProveChunk> _chunks = new(chunks);
        public ProveChunk Current { get; private set; } = null!;
        public Task<bool> MoveNext(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_chunks.TryDequeue(out ProveChunk? chunk)) return Task.FromResult(false);
            Current = chunk;
            return Task.FromResult(true);
        }
    }
}
