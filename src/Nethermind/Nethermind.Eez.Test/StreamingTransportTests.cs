// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Eez.Execution;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using L1PostingContext = Nethermind.Eez.Prove.V2.L1PostingContext;
using NUnit.Framework;
using StreamBlock = Nethermind.Eez.Prove.V2.Block;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Test;

public class StreamingTransportTests
{
    private StreamingSessionRegistry _registry = null!;
    private BackendAdmission _admission = null!;
    private Reader _input = null!;
    private Writer _output = null!;
    private TaskCompletionSource<ExecutedStreamingBlock> _worker = null!;
    private StreamingTransport _transport = null!;
    private int _executions;
    private CancellationToken _workerCancellation;

    [SetUp]
    public void SetUp()
    {
        _registry = new StreamingSessionRegistry(new StreamingRegistryLimits(4, 4, 16000, TimeSpan.FromMinutes(10)),
            new StreamingSessionLimits(8, 1000, 16), TimeProvider.System);
        _admission = new BackendAdmission(NullLogger<BackendAdmission>.Instance);
        _input = new Reader();
        _output = new Writer();
        _worker = new TaskCompletionSource<ExecutedStreamingBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        _executions = 0;
        _workerCancellation = default;
        _transport = Transport(Execute);
        _input.Add(() => new ClientFrame { RequestId = 1, Begin = Anchor() });
    }

    [TearDown]
    public void TearDown() => _registry.Dispose();

    [TestCase((int)StreamingSettlementProfile.RollupXNative, "Received message exceeds the maximum configured message size.", StatusCode.OutOfRange, TestName = "Run_RollupXReaderSizeLimit_IsOutOfRange")]
    [TestCase((int)StreamingSettlementProfile.RollupXNative, "other capacity", StatusCode.ResourceExhausted, TestName = "Run_RollupXReaderOtherCapacity_RemainsResourceExhausted")]
    [TestCase((int)StreamingSettlementProfile.RollupXNative, "Received message exceeds the maximum configured message size. suffix", StatusCode.ResourceExhausted, TestName = "Run_RollupXReaderNonexactSizeError_RemainsResourceExhausted")]
    [TestCase((int)StreamingSettlementProfile.Rollup0Calldata, "Received message exceeds the maximum configured message size.", StatusCode.ResourceExhausted, TestName = "Run_Rollup0ReaderSizeLimit_RemainsResourceExhausted")]
    public async Task Run_ReaderCapacityException_MapsOnlyExactSdkMessageSizeInSelectedProfile(int profile, string detail, StatusCode expected)
    {
        _registry.Dispose();
        _registry = new StreamingSessionRegistry(new StreamingRegistryLimits(4, 4, 16000, TimeSpan.FromMinutes(10)),
            new StreamingSessionLimits(8, 1000, 16), TimeProvider.System, (StreamingSettlementProfile)profile);
        _transport = Transport(Execute);
        _input = new Reader();
        _input.AddAsync(() => Task.FromException<ClientFrame?>(new RpcException(new Status(StatusCode.ResourceExhausted, detail))));
        RpcException error = (await Assert.ThrowsAsync<RpcException>(() => _transport.Run(_input, _output, "composer", CancellationToken.None)))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.StatusCode, Is.EqualTo(expected));
            Assert.That(_executions, Is.Zero, "reader rejection cannot admit execution or sign a claim");
        }
    }

    [Test]
    public async Task Run_RecordedBlock_UsesRealStatelessExecutorBeforeValidation()
    {
        const string fixture = "stateless-block-13";
        using Witness witness = StatelessFixtures.ReadWitness(fixture, "witness-13.json");
        byte[] encoded = StatelessFixtures.ReadBlock(fixture, "block-13.rlp");
        Block block = Rlp.Decode<Block>(encoded)!;
        BlockHeader parent = Rlp.Decode<BlockHeader>(witness.Headers.Single())!;
        _registry.Dispose();
        _registry = new StreamingSessionRegistry(new StreamingRegistryLimits(4, 4, 128 * 1024 * 1024, TimeSpan.FromMinutes(10)),
            new StreamingSessionLimits(8, 16 * 1024 * 1024, 100_000), TimeProvider.System);
        StreamingBlockExecutor executor = new(StatelessFixtures.ReadSpecProvider(fixture, "chain-config.json"), EezChainRules.Rollup0, LimboLogs.Instance);
        _transport = new StreamingTransport(_registry, _admission, executor,
            new StreamingTransportLimits(2, 16 * 1024 * 1024, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            1, NullLogger<StreamingTransport>.Instance);
        _input = new Reader();
        _input.Add(() => new ClientFrame
        {
            Begin = new Begin
            {
                RollupId = 1,
                AnchorNumber = parent.Number,
                AnchorHash = ByteString.CopyFrom(parent.Hash!.Bytes),
                AnchorStateRoot = ByteString.CopyFrom(parent.StateRoot!.Bytes)
            }
        });
        BlockWitness data = new()
        {
            Number = block.Number,
            Hash = ByteString.CopyFrom(block.Hash!.Bytes),
            ParentHash = ByteString.CopyFrom(block.ParentHash!.Bytes),
            Rlp = ByteString.CopyFrom(encoded),
            Witness = new ExecutionWitness()
        };
        data.Witness.State.Add(witness.State.Select(static item => ByteString.CopyFrom(item)));
        data.Witness.Codes.Add(witness.Codes.Select(static item => ByteString.CopyFrom(item)));
        data.Witness.Keys.Add(witness.Keys.Select(static item => ByteString.CopyFrom(item)));
        data.Witness.Headers.Add(witness.Headers.Select(static item => ByteString.CopyFrom(item)));
        _input.Add(() => Bound(2, new StreamBlock { Data = data }));
        TaskCompletionSource<ClientFrame?> eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.AddAsync(() => eof.Task);
        _output.OnWrite = frame => { if (frame.Validated is not null) eof.SetResult(null); return Task.CompletedTask; };
        await Run();
        Assert.That((_output.Frames[1].RequestId, _output.Frames[1].Validated.Number, _output.Frames[1].Validated.Hash, _output.Frames[1].Validated.PostStateRoot),
            Is.EqualTo((2UL, 13UL, ByteString.CopyFrom(Convert.FromHexString("16b64a78e9b3e0d533cafe81f9121735f6a2c8122c69b0bb5994ee75fe7bface")),
                ByteString.CopyFrom(Convert.FromHexString("f09d8f7da5bc5036f8dd9536c953e2212390a46fb3e553ece2b7d419131537b1")))),
            "the transport validates the independently recorded block hash and post-state root through the real executor");
    }

    [Test]
    public async Task Run_ValidatedBlock_CorrelatesResultAndRetainsProgress()
    {
        _worker.SetResult(Executed());
        _input.Add(BlockFrame);
        await Run();
        Assert.That((_output.Frames.Count, _output.Frames[1].RequestId, _output.Frames[1].Validated.Hash),
            Is.EqualTo((2, 2UL, Hash(11))), "execution precedes the correlated validation response");
        ServerFrame resumed = Resume();
        Assert.That(resumed.Ready.ValidatedThrough, Is.EqualTo(11), "EOF retains only validated progress for the authenticated owner");
    }

    [Test]
    public async Task Run_ClientHalfCloses_DrainsAcceptedWorkerAndReturnsValidation()
    {
        _input.Add(BlockFrame);
        Task stream = Run();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((stream.IsCompleted, _workerCancellation.IsCancellationRequested), Is.EqualTo((false, false)),
            "client half-close must wait for accepted execution rather than cancel it");
            Assert.That(_admission.TryAcquire(), Is.Null, "the accepted backend worker keeps its slot while the client has half-closed");
        }
        _worker.SetResult(Executed());
        await stream;
        await _admission.WaitUntilIdle();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_output.Frames.Count, _output.Frames[1].RequestId, _output.Frames[1].Validated.Number), Is.EqualTo((2, 2UL, 11UL)),
            "half-close drains and acknowledges accepted work without producing a proof");
            Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(11), "fully validated progress survives a half-closed stream");
        }
    }

    [Test]
    public async Task Run_CancelBeforeExecutionFinishes_HoldsBackendAndFencesResult()
    {
        _input.Add(BlockFrame);
        _input.Add(() => Bound(3, new Cancel()));
        await Run();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_workerCancellation.IsCancellationRequested, Is.True, "Cancel cancels its attachment worker");
            Assert.That(_admission.TryAcquire(), Is.Null, "the backend remains owned until the real worker drains");
            Assert.That(_output.Frames.Count, Is.EqualTo(2), "no unfinished block is acknowledged");
        }
        _worker.SetResult(Executed());
        await _admission.WaitUntilIdle();
        using BackendAdmission.BackendLease? retry = _admission.TryAcquire();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retry, Is.Not.Null, "actual completion releases global admission");
            Assert.That(_output.Frames[^1].Cancelled, Is.Not.Null, "Cancel is the last response");
        }
    }

    [Test]
    public async Task Run_RewindDuringExecution_DropsOldResultAndAllowsRetry()
    {
        _input.Add(BlockFrame);
        _input.Add(() => Bound(3, new Rewind { AncestorHash = Hash(1) }));
        _output.OnWrite = frame =>
        {
            if (frame.RequestId == 3) _worker.SetResult(Executed());
            return Task.CompletedTask;
        };
        _input.AddAsync(async () =>
        {
            await _admission.WaitUntilIdle();
            return BlockFrame();
        });
        await Run();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_executions, _output.Frames.Count, _output.Frames[1].Epoch, _output.Frames[2].Validated.Number),
            Is.EqualTo((2, 3, 2UL, 11UL)), "only the retry in the new epoch receives validation");
            Assert.That(_output.Frames[2].Epoch, Is.EqualTo(2), "the rewound operation cannot publish its old epoch");
        }
    }

    [Test]
    public async Task Run_ResumeFromAnotherAttachment_ClosesOldStreamWithoutLateResponse()
    {
        _input.Add(BlockFrame);
        _input.Add(() =>
        {
            Resume();
            _worker.SetResult(Executed());
            return null;
        });
        await Run();
        await _admission.WaitUntilIdle();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_output.Frames.Count, Is.EqualTo(1), "no response from the superseded worker is written");
            Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "a superseded execution cannot advance retained state");
        }
    }

    [TestCase(false, TestName = "Run_KnownBackendRejection_AllowsRetry")]
    [TestCase(true, TestName = "Run_UnexpectedBackendFailure_HidesDetailsAndAllowsRetry")]
    public async Task Run_BackendFailure_ReturnsCorrelatedRejectionAndReleasesSlot(bool unexpected)
    {
        _worker.SetException(unexpected ? new InvalidOperationException("private failure detail") :
            new RpcException(new Status(StatusCode.FailedPrecondition, "block validation rejected")));
        _input.Add(BlockFrame);
        _output.OnWrite = frame =>
        {
            if (frame.Rejected is not null) _worker = new TaskCompletionSource<ExecutedStreamingBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (frame.Rejected is not null) _worker.SetResult(Executed());
            return Task.CompletedTask;
        };
        _input.Add(BlockFrame);
        await Run();
        ServerFrame rejected = _output.Frames[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That((rejected.RequestId, rejected.Rejected.Code, rejected.Rejected.ValidatedThrough),
            Is.EqualTo((2UL, (int)(unexpected ? StatusCode.Internal : StatusCode.FailedPrecondition), 10UL)), "a rejection keeps its request correlation and previous progress");
            Assert.That(rejected.Rejected.Message, Does.Not.Contain("private"), "internal backend details do not cross the wire");
            Assert.That((_executions, _output.Frames[2].Validated.Number), Is.EqualTo((2, 11UL)), "draining a failed worker permits the same block to retry");
        }
    }

    [Test]
    public async Task Run_SharedBackendIsBusy_WaitsBeforeStartingWorker()
    {
        using BackendAdmission.BackendLease occupied = _admission.TryAcquire()!;
        _worker.SetResult(Executed());
        _input.Add(BlockFrame);
        Task stream = Run();
        Assert.That((_executions, stream.IsCompleted, _admission.TryAcquire()), Is.EqualTo((0, false, (BackendAdmission.BackendLease?)null)),
            "waiting v2 requests cannot execute next to the current backend owner");
        occupied.Dispose();
        await stream;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_executions, _output.Frames[1].Validated.Number), Is.EqualTo((1, 11UL)), "release permits exactly one waiting execution");
            Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(11));
        }
    }

    [Test]
    public async Task Run_PipelinedBlocksAndFinalize_KeepOneQueuedFrameAndDrainInOrder([Values(2, 7)] int blockCount)
    {
        TaskCompletionSource<ExecutedStreamingBlock> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport = Transport((operation, _) =>
        {
            _executions++;
            if (operation.Block!.Number == 11) return _worker.Task;
            ulong number = operation.Block.Number;
            Assert.That(operation.PreStateRoot, Is.EqualTo(Hash((byte)(number + 19))), "dependent work sees the committed predecessor state");
            if (number == 12)
            {
                secondStarted.SetResult();
                return second.Task;
            }
            return Task.FromResult(new ExecutedStreamingBlock(number, Hash((byte)number), Hash((byte)(number - 1)),
                Hash((byte)(number + 19)), Hash((byte)(number + 20))));
        }, finalize: (_, _) => Task.FromResult(TestProof()));
        _input.Add(BlockFrame);
        _input.Add(() =>
        {
            queued.SetResult();
            ClientFrame frame = Bound(3, new StreamBlock
            {
                Data = new BlockWitness
                {
                    Number = 12,
                    Hash = Hash(12),
                    ParentHash = Hash(11),
                    Rlp = Hash(12),
                    Witness = new ExecutionWitness(),
                }
            });
            return frame;
        });
        for (int index = 2; index < blockCount; index++)
        {
            ulong number = (ulong)(11 + index);
            _input.Add(() => Bound(number - 9, new StreamBlock
            {
                Data = new BlockWitness
                {
                    Number = number,
                    Hash = Hash((byte)number),
                    ParentHash = Hash((byte)(number - 1)),
                    Rlp = Hash((byte)number),
                    Witness = new ExecutionWitness(),
                }
            }));
        }
        ulong terminal = (ulong)(10 + blockCount);
        _input.Add(() => Bound((ulong)(blockCount + 2), new Finalize
        {
            FromBlock = 11,
            ToBlock = terminal,
            TerminalHash = Hash((byte)terminal),
            Settlement = new SettlementInput { AbiCalldata = Hash(90) },
        }));
        Task stream = Run();
        await queued.Task;
        Assert.That((_input.Reads, _executions, _output.Frames.Count), Is.EqualTo((3, 1, 1)), "one queued data frame stops further reads");
        _worker.SetResult(Executed());
        await secondStarted.Task;
        Assert.That((_output.Frames[^1].RequestId, _output.Frames[^1].Validated.Number), Is.EqualTo((2UL, 11UL)));
        second.SetResult(new ExecutedStreamingBlock(12, Hash(12), Hash(11), Hash(31), Hash(32)));
        await stream;
        await _admission.WaitUntilIdle();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_output.Frames.Select(static frame => frame.RequestId),
            Is.EqualTo(Enumerable.Range(1, blockCount + 2).Select(static number => (ulong)number)), "every queued request receives one ordered response");
            Assert.That(_output.Frames.Skip(1).Take(blockCount).Select(static frame => frame.Validated.Number),
                Is.EqualTo(Enumerable.Range(11, blockCount).Select(static number => (ulong)number)), "each block validates after its predecessor");
            Assert.That((_output.Frames[^1].Proof, _executions), Is.EqualTo((TestProof(), blockCount)),
                "pipelined validation and the terminal proof use one execution slot");
        }
    }

    [TestCase(false, TestName = "Run_QueueWaitDeadline_RejectsWithoutStartingWorker")]
    [TestCase(true, TestName = "Run_QueueWaitAndExecution_ShareOneOperationDeadline")]
    public async Task Run_QueuedOperation_UsesOriginalDeadline(bool execute)
    {
        ManualTime time = new();
        using BackendAdmission.BackendLease occupied = _admission.TryAcquire()!;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport = Transport((operation, token) => { started.SetResult(); return Execute(operation, token); },
            new StreamingTransportLimits(2, 1000, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)), time);
        _input.Add(BlockFrame);
        Task stream = Run();
        Assert.That((_executions, stream.IsCompleted), Is.EqualTo((0, false)), "the pending frame waits on shared admission");
        time.Advance(TimeSpan.FromMinutes(1));
        if (execute)
        {
            occupied.Dispose();
            await started.Task;
        }
        time.Advance(TimeSpan.FromMinutes(1));
        await stream;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_output.Frames[^1].RequestId, _output.Frames[^1].Rejected.Code, _executions),
            Is.EqualTo((2UL, (int)StatusCode.DeadlineExceeded, execute ? 1 : 0)), "waiting does not renew the operation deadline");
            Assert.That(_admission.TryAcquire(), Is.Null, "deadline expiry does not release another owner or an undrained worker");
        }
        if (execute) _worker.SetResult(Executed());
        else occupied.Dispose();
        await _admission.WaitUntilIdle();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_output.Frames.Exists(static frame => frame.Validated is not null), Is.False);
            Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "expired queued work cannot run later or commit stale progress");
        }
    }

    [Test]
    public async Task Run_UnauthorizedQueuedFrame_DoesNotCancelAcceptedExecution()
    {
        _input.Add(BlockFrame);
        _input.Add(() => new ClientFrame { RequestId = 3, SessionId = Hash(99), Epoch = 1, Block = new StreamBlock { Data = new BlockWitness() } });
        _output.OnWrite = frame =>
        {
            if (frame.Rejected is not null)
            {
                Assert.That(_workerCancellation.IsCancellationRequested, Is.False);
                _worker.SetResult(Executed());
            }
            return Task.CompletedTask;
        };
        await Run();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_output.Frames[1].Rejected.Code, _output.Frames[1].SessionId.IsEmpty, _output.Frames[1].Rejected.ValidatedHash.IsEmpty),
            Is.EqualTo(((int)StatusCode.PermissionDenied, true, true)));
            Assert.That((_output.Frames[2].RequestId, _output.Frames[2].Validated.Number), Is.EqualTo((2UL, 11UL)));
        }
    }

    [Test]
    public async Task Run_AlreadyReadCancel_FencesSimultaneouslyCompletedWorker()
    {
        _input.Add(BlockFrame);
        _input.Add(() => { _worker.SetResult(Executed()); return Bound(3, new Cancel()); });
        await Run();
        await _admission.WaitUntilIdle();
        Assert.That((_output.Frames.Count, _output.Frames[^1].RequestId, _output.Frames[^1].Cancelled is not null), Is.EqualTo((2, 3UL, true)),
            "an accepted control fences completed work before its response can be sent");
    }

    [TestCase(false, StatusCode.Aborted)]
    [TestCase(true, StatusCode.PermissionDenied)]
    public async Task Run_ResumeWithQueuedData_RetiresOldStreamAndKeepsBackendUntilDrain(bool copyNewEpoch, StatusCode code)
    {
        TaskCompletionSource queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.Add(BlockFrame);
        _input.Add(() =>
        {
            ClientFrame frame = Bound(3, new StreamBlock { Data = new BlockWitness() });
            if (copyNewEpoch) frame.Epoch = Resume().Epoch;
            queued.SetResult();
            return frame;
        });
        Task stream = Run();
        await queued.Task;
        if (!copyNewEpoch) Resume();
        await stream;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_output.Frames.Count, _output.Frames[^1].RequestId, _output.Frames[^1].Rejected.Code, _output.Frames[^1].SessionId.IsEmpty),
            Is.EqualTo((2, 3UL, (int)code, true)));
            Assert.That((_workerCancellation.IsCancellationRequested, _admission.TryAcquire()), Is.EqualTo((true, (BackendAdmission.BackendLease?)null)));
        }
        _worker.SetResult(Executed());
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "neither running nor queued old-epoch work advances the resumed session");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Run_RetiredStream_RejectsEveryOldRequestWithoutMutatingNewOwner(bool dataFirst)
    {
        ServerFrame newer = null!;
        _input.Add(BlockFrame);
        ClientFrame OldFrame(ulong request, object operation)
        {
            ClientFrame frame = new() { SessionId = _output.Frames[0].SessionId, Epoch = _output.Frames[0].Epoch, RequestId = request };
            switch (operation)
            {
                case StreamBlock block: frame.Block = block; break;
                case Finalize finalize: frame.Finalize = finalize; break;
                case Rewind rewind: frame.Rewind = rewind; break;
                case Cancel cancel: frame.Cancel = cancel; break;
            }
            return frame;
        }
        _input.Add(() =>
        {
            newer = Resume();
            return OldFrame(3, dataFirst ? new StreamBlock { Data = new BlockWitness() } : new Cancel());
        });
        _input.Add(() => OldFrame(4, new Finalize()));
        _input.Add(() => OldFrame(5, new Rewind()));
        _input.Add(() => OldFrame(6, new Cancel()));
        _input.Add(() =>
        {
            ClientFrame frame = OldFrame(7, new StreamBlock { Data = new BlockWitness() });
            frame.Epoch = newer.Epoch;
            return frame;
        });
        await Run();
        Assert.That((_executions, _workerCancellation.IsCancellationRequested, _output.Frames.Count), Is.EqualTo((1, true, 6)));
        for (int i = 1; i < _output.Frames.Count; i++)
        {
            ServerFrame rejected = _output.Frames[i];
            Assert.That((rejected.RequestId, rejected.Rejected.Code, rejected.SessionId.IsEmpty, rejected.Epoch,
                rejected.Rejected.ValidatedThrough, rejected.Rejected.ValidatedHash.IsEmpty),
                Is.EqualTo(((ulong)i + 2, (int)(i == 5 ? StatusCode.PermissionDenied : StatusCode.Aborted), true, 0UL, 0UL, true)));
        }
        Assert.That(_admission.TryAcquire(), Is.Null, "retirement does not release an executor that has not drained");
        _worker.SetResult(Executed());
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "old controls and late worker results cannot change the resumed session");
    }

    [Test]
    public async Task Run_RetiredRequests_DoNotRenewAbsoluteDeadline()
    {
        ManualTime time = new();
        _transport = Transport(Execute, time: time);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ClientFrame?> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.Add(() =>
        {
            ClientFrame frame = Bound(2, new Cancel());
            Resume();
            return frame;
        });
        _input.AddAsync(() => next.Task);
        _input.AddAsync(() => new TaskCompletionSource<ClientFrame?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        _output.OnWrite = frame =>
        {
            if (frame.RequestId == 2) first.TrySetResult();
            if (frame.RequestId == 3) second.TrySetResult();
            return Task.CompletedTask;
        };
        Task stream = Run();
        await first.Task;
        time.Advance(TimeSpan.FromSeconds(40));
        next.SetResult(new ClientFrame { SessionId = _output.Frames[0].SessionId, Epoch = _output.Frames[0].Epoch, RequestId = 3, Rewind = new Rewind() });
        await second.Task;
        time.Advance(TimeSpan.FromSeconds(20));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => stream))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((exception.StatusCode, _executions, _output.Frames.Count), Is.EqualTo((StatusCode.DeadlineExceeded, 0, 3)),
            "new rejected traffic cannot extend a retired connection beyond its first retirement deadline");
            Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10));
        }
    }

    [TestCase(false, TestName = "Run_UnsupportedBlobs_DoNotWaitForBusyBackend")]
    [TestCase(true, TestName = "Run_UnsupportedPostingContext_DoesNotWaitForBusyBackend")]
    public async Task Run_UnsupportedSettlement_RejectsBeforeWaitingForAdmission(bool context)
    {
        using BackendAdmission.BackendLease occupied = _admission.TryAcquire()!;
        _transport = Transport(Execute, finalize: (_, _) => Task.FromResult(TestProof()));
        _input.Add(() =>
        {
            SettlementInput settlement = new() { AbiCalldata = Hash(90) };
            if (context) settlement.PostingContext = new L1PostingContext();
            else settlement.Blobs.Add(Hash(60));
            return Bound(2, new Finalize { FromBlock = 11, ToBlock = 11, TerminalHash = Hash(11), Settlement = settlement });
        });
        await Run();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_output.Frames[^1].RequestId, _output.Frames[^1].Rejected.Code, _output.Frames[^1].Rejected.ValidatedThrough, _executions),
            Is.EqualTo((2UL, (int)StatusCode.Unimplemented, 10UL, 0)), "unsupported profile data never waits on execution or attempts range admission");
            Assert.That(_admission.TryAcquire(), Is.Null, "profile refusal leaves the current backend owner untouched");
        }
    }

    [Test]
    public async Task Run_WriterHasBackpressure_DoesNotReadAheadOrStartExecution()
    {
        TaskCompletionSource writable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _output.OnWrite = frame => frame.Ready is not null ? writable.Task : Task.CompletedTask;
        _worker.SetResult(Executed());
        _input.Add(BlockFrame);
        Task stream = Run();
        Assert.That((_input.Reads, _executions, stream.IsCompleted), Is.EqualTo((1, 0, false)),
            "one serialized write blocks further frame admission");
        writable.SetResult();
        await stream;
        Assert.That(_output.Frames[1].Validated.Number, Is.EqualTo(11), "execution starts after the Ready response drains");
    }

    [Test]
    public async Task Run_WriteFailsWhileWorkerIsActive_DetachesAndKeepsSlotUntilDrain()
    {
        _input.Add(BlockFrame);
        _input.Add(() => Bound(3, null));
        _output.OnWrite = frame => frame.RequestId == 3 ? Task.FromException(new InvalidOperationException("closed writer")) : Task.CompletedTask;
        await Assert.ThrowsAsync<InvalidOperationException>(Run);
        Assert.That(_admission.TryAcquire(), Is.Null, "a transport error cannot release an active worker");
        _worker.SetException(new InvalidOperationException("late worker failure"));
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "failed response transport preserves only committed progress");
    }

    [Test]
    public async Task Run_FinalizeWithoutAuthorization_RejectsWithoutProofOrBackendUse()
    {
        _worker.SetResult(Executed());
        _input.Add(BlockFrame);
        _input.Add(() => Bound(3, new Finalize
        {
            FromBlock = 11,
            ToBlock = 11,
            TerminalHash = Hash(11),
            Settlement = new SettlementInput { AbiCalldata = Hash(90) },
        }));
        await Run();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_executions, _output.Frames.Count, _output.Frames[^1].Rejected.Code), Is.EqualTo((1, 3, (int)StatusCode.Unimplemented)),
            "settlement claims are not signed before independent authorization exists");
            Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(11), "unsupported finalization preserves useful validated progress");
        }
    }

    [Test]
    public async Task Run_FinalizeWithBackend_ReturnsTerminalProof()
    {
        _worker.SetResult(Executed());
        int finalizations = 0;
        _transport = Transport(Execute, finalize: (_, _) => { finalizations++; return Task.FromResult(TestProof()); });
        _input.Add(BlockFrame);
        _input.Add(FinalizeFrame);
        _input.Add(() => Bound(4, new Cancel()));
        await Run();
        Assert.That((finalizations, _output.Frames[^1].RequestId, _output.Frames[^1].Proof, _input.Reads),
            Is.EqualTo((1, 3UL, TestProof(), 3)), "Proof is correlated and terminal");
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(11), "proof completion retains the reusable validated prefix");
    }

    [TestCase("Cancel", TestName = "Run_CancelDuringProof_FencesResponseUntilDrain")]
    [TestCase("Rewind", TestName = "Run_RewindDuringProof_FencesResponseUntilDrain")]
    [TestCase("Resume", TestName = "Run_ResumeDuringProof_FencesResponseUntilDrain")]
    public async Task Run_ControlChangeDuringProof_PreventsLateResponse(string control)
    {
        _worker.SetResult(Executed());
        TaskCompletionSource<Proof> proof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport = Transport(Execute, finalize: (_, _) => proof.Task);
        _input.Add(BlockFrame);
        _input.Add(FinalizeFrame);
        _input.Add(() => control switch
        {
            "Cancel" => Bound(4, new Cancel()),
            "Rewind" => Bound(4, new Rewind { AncestorHash = Hash(1) }),
            _ => ResumeAndClose(),
        });
        Task stream = Run();
        await stream;
        Assert.That(_admission.TryAcquire(), Is.Null, "an unfinished proof worker owns the shared slot after the control response");
        proof.SetResult(TestProof());
        await _admission.WaitUntilIdle();
        Assert.That(_output.Frames.Exists(static frame => frame.Proof is not null), Is.False, "a stale proof must never reach the writer");
    }

    [TestCase(false, TestName = "Run_ResumeDuringBlockedProofWrite_FencesOldResponse")]
    [TestCase(true, TestName = "Run_ResumeThenCancelDuringBlockedProofWrite_FencesOldResponse")]
    public async Task Run_ControlChangeDuringTerminalProofWrite_CancelsOldWrite(bool cancel)
    {
        _worker.SetResult(Executed());
        _transport = Transport(Execute, finalize: (_, _) => Task.FromResult(TestProof()));
        _input.Add(BlockFrame);
        _input.Add(FinalizeFrame);
        TaskCompletionSource writable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _output.OnWrite = frame =>
        {
            if (frame.Proof is null) return Task.CompletedTask;
            blocked.SetResult();
            return writable.Task;
        };
        Task stream = Run();
        await blocked.Task;
        StreamingConnection newer = new(_registry, "composer", 1);
        ServerFrame ready = newer.Dispatch(new ClientFrame { SessionId = _output.Frames[0].SessionId, Resume = new Resume() }).Response!;
        Assert.That(ready.Ready.ValidatedThrough, Is.EqualTo(11), "the new authenticated attachment accepts the retained proof prefix");
        if (cancel)
        {
            ServerFrame cancelled = newer.Dispatch(new ClientFrame { SessionId = ready.SessionId, Epoch = ready.Epoch, Cancel = new Cancel() }).Response!;
            Assert.That(cancelled.Cancelled, Is.Not.Null, "Cancel is accepted before the old proof writer resumes");
        }
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => stream))!;
        writable.SetResult();
        await _admission.WaitUntilIdle();
        Assert.That((exception.StatusCode, _output.Frames.Exists(static frame => frame.Proof is not null)), Is.EqualTo((StatusCode.Aborted, false)),
            "a completed but unsent proof is fenced by later controls before their response is accepted");
    }

    [Test]
    public async Task Run_ProofDeadlineExpires_PreservesPrefixAndRetainsSlotUntilDrain()
    {
        ManualTime time = new();
        TaskCompletionSource<Proof> proof = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker.SetResult(Executed());
        _transport = Transport(Execute, new StreamingTransportLimits(2, 1000, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)), time,
            (_, _) => { started.SetResult(); return proof.Task; });
        _input.Add(BlockFrame);
        _input.Add(FinalizeFrame);
        TaskCompletionSource<ClientFrame?> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.AddAsync(() => next.Task);
        _output.OnWrite = frame => { if (frame.Rejected is not null) next.SetResult(null); return Task.CompletedTask; };
        Task stream = Run();
        await started.Task;
        time.Advance(TimeSpan.FromMinutes(1));
        await stream;
        Assert.That((_output.Frames[^1].Rejected.Code, _admission.TryAcquire()), Is.EqualTo(((int)StatusCode.DeadlineExceeded, (BackendAdmission.BackendLease?)null)),
            "response deadlines retain ownership of real proof execution");
        proof.SetResult(TestProof());
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(11), "failed finalization preserves execution progress");
    }

    [Test]
    public async Task Run_SettlementRejection_CarriesActionableDetailsAndAllowsRetry()
    {
        _worker.SetResult(Executed());
        int attempts = 0;
        ByteString details = ByteString.CopyFrom(new ProveFailure { Inbound = new InboundFailure { EntryIndex = 2, EntryHash = Hash(20) } }.ToByteArray());
        _transport = Transport(Execute, finalize: (_, _) => ++attempts == 1
            ? Task.FromException<Proof>(AttestationPipeline.Failure(StatusCode.FailedPrecondition, "settlement validation rejected", ProveFailure.Parser.ParseFrom(details)))
            : Task.FromResult(TestProof()));
        _input.Add(BlockFrame);
        _input.Add(FinalizeFrame);
        _input.Add(FinalizeFrame);
        await Run();
        Assert.That((_output.Frames[2].Rejected.Details, _output.Frames[^1].Proof, attempts), Is.EqualTo((details, TestProof(), 2)),
            "the exact actionable failure survives v2 and the same validated range can retry");
    }

    private ClientFrame? ResumeAndClose() { Resume(); return null; }

    private ClientFrame FinalizeFrame() => Bound(3, new Finalize
    {
        FromBlock = 11,
        ToBlock = 11,
        TerminalHash = Hash(11),
        Settlement = new SettlementInput { AbiCalldata = Hash(90) },
    });

    private static Proof TestProof() => new() { PublicInputsHash = Hash(80), Proof_ = ByteString.CopyFrom(new byte[65]) };

    [Test]
    public async Task Run_UnknownOperation_IsCorrelatedAndStreamRemainsUsable()
    {
        _input.Add(() => Bound(3, null));
        _worker.SetResult(Executed());
        _input.Add(BlockFrame);
        await Run();
        Assert.That((_output.Frames[1].RequestId, _output.Frames[1].Rejected.Code, _output.Frames[2].Validated.Number),
            Is.EqualTo((3UL, (int)StatusCode.Unimplemented, 11UL)), "an unsupported operation does not consume the session worker");
    }

    [Test]
    public async Task Run_MessageOverLimit_RejectsBeforeExecution()
    {
        _transport = Transport(Execute, new StreamingTransportLimits(2, 100, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));
        _input.Add(() =>
        {
            ClientFrame frame = BlockFrame();
            frame.Block.Data.Rlp = ByteString.CopyFrom(new byte[200]);
            return frame;
        });
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(Run))!;
        Assert.That((exception.StatusCode, _executions), Is.EqualTo((StatusCode.ResourceExhausted, 0)), "oversized decoded frames cannot enter the backend");
    }

    [Test]
    public async Task Run_ConnectionLimit_RejectsSecondStreamAndRecoversAfterDetach()
    {
        _transport = Transport(Execute, new StreamingTransportLimits(1, 1000, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));
        TaskCompletionSource writable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _output.OnWrite = _ => writable.Task;
        Task first = Run();
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => _transport.Run(new Reader(), new Writer(), "other", CancellationToken.None)))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.ResourceExhausted), "unbound and slow streams count toward the connection limit");
        writable.SetResult();
        await first;
        await _transport.Run(new Reader(), new Writer(), "other", CancellationToken.None);
    }

    [Test]
    public async Task Run_CallerCancellation_HoldsAdmissionUntilWorkerDrain()
    {
        using CancellationTokenSource closed = new();
        _input.Add(BlockFrame);
        _input.Add(() => { closed.Cancel(); return null; });
        await Assert.CatchAsync<OperationCanceledException>(() => _transport.Run(_input, _output, "composer", closed.Token));
        Assert.That(_admission.TryAcquire(), Is.Null, "request cancellation keeps execution ownership");
        _worker.SetResult(Executed());
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "caller cancellation cannot commit the unfinished operation");
    }

    [Test]
    public async Task ReleaseAfter_FailedWorker_ObservesFailureAndCleansUpExactlyOnce()
    {
        BackendAdmission.BackendLease lease = _admission.TryAcquire()!;
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int cleanups = 0;
        Task drain = lease.ReleaseAfter(pending.Task, () => cleanups++);
        Assert.That(_admission.WaitUntilIdle().IsCompleted, Is.False, "idle waits for the real worker rather than the response deadline");
        pending.SetException(new InvalidOperationException("worker failure"));
        await drain;
        lease.Dispose();
        Assert.That((cleanups, _admission.WaitUntilIdle().IsCompleted), Is.EqualTo((1, true)), "cleanup and release finish once after failure");
    }

    [TestCase(false, TestName = "RejectUnstarted_BusyBlockResumed_ReleasesAdmission")]
    [TestCase(true, TestName = "RejectUnstarted_UnsupportedFinalizeResumed_ReleasesAdmission")]
    public void RejectUnstarted_AttachmentChanges_ReleasesAdmittedOperation(bool finalize)
    {
        StreamingConnection original = new(_registry, "composer", 1);
        ServerFrame ready = original.Dispatch(new ClientFrame { Begin = Anchor() }).Response!;
        ClientFrame frame = new()
        {
            SessionId = ready.SessionId,
            Epoch = ready.Epoch,
            RequestId = 2,
            Block = new StreamBlock { Data = new BlockWitness { Number = 11, Hash = Hash(11), ParentHash = Hash(1), Rlp = Hash(11), Witness = new ExecutionWitness() } }
        };
        SessionOperation operation = original.Dispatch(frame).Operation!;
        if (finalize)
        {
            original.CommitBlock(operation, Executed());
            original.FinishOperation(operation);
            frame.Finalize = new Finalize { FromBlock = 11, ToBlock = 11, TerminalHash = Hash(11), Settlement = new SettlementInput() };
            operation = original.Dispatch(frame).Operation!;
        }
        StreamingConnection resumed = new(_registry, "composer", 1);
        ServerFrame newer = resumed.Dispatch(new ClientFrame { SessionId = ready.SessionId, Resume = new Resume() }).Response!;
        RpcException exception = Assert.Throws<RpcException>(() => StreamingTransport.RejectUnstarted(original, operation,
            new Status(finalize ? StatusCode.Unimplemented : StatusCode.ResourceExhausted, "rejected")))!;
        Assert.That((exception.StatusCode, operation.IsDisposed), Is.EqualTo((StatusCode.Aborted, true)), "a failed stale rejection must release the admitted operation");
        frame.Epoch = newer.Epoch;
        SessionOperation retry = resumed.Dispatch(frame).Operation!;
        Assert.That(retry, Is.Not.Null, "the new attachment is not blocked by an orphaned operation");
        resumed.FinishOperation(retry);
    }

    [TestCase(false, TestName = "Run_ResumeDuringBlockedWrite_CancelsOldWrite")]
    [TestCase(true, TestName = "Run_RegistryRetiresDuringBlockedWrite_CancelsOldWrite")]
    public async Task Run_SessionRevokedDuringBlockedWrite_CancelsOldWrite(bool retire)
    {
        _worker.SetResult(Executed());
        _input.Add(BlockFrame);
        TaskCompletionSource writable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _output.OnWrite = frame =>
        {
            if (frame.Validated is null) return Task.CompletedTask;
            blocked.SetResult();
            return writable.Task;
        };
        Task stream = Run();
        await blocked.Task;
        if (retire) _registry.Dispose();
        else Resume();
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => stream))!;
        Assert.That((exception.StatusCode, _output.Frames.Count), Is.EqualTo((StatusCode.Aborted, 1)), "Resume cancels an unsent response on the old attachment");
        writable.SetResult();
        if (!retire) Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(11), "the resumed owner can recover the block committed before transport failure");
    }

    [Test]
    public async Task Run_OperationDeadlineExpires_HoldsSlotUntilRealWorkerDrain()
    {
        ManualTime time = new();
        _transport = Transport(Execute, new StreamingTransportLimits(2, 1000, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)), time);
        _input.Add(BlockFrame);
        TaskCompletionSource<ClientFrame?> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.AddAsync(() => next.Task);
        _output.OnWrite = frame =>
        {
            if (frame.Rejected is not null) next.SetResult(null);
            return Task.CompletedTask;
        };
        Task stream = Run();
        time.Advance(TimeSpan.FromMinutes(1));
        await stream;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((_output.Frames[1].Rejected.Code, _workerCancellation.IsCancellationRequested), Is.EqualTo(((int)StatusCode.DeadlineExceeded, true)),
            "an operation deadline is reported without validating its block");
            Assert.That(_admission.TryAcquire(), Is.Null, "the response timeout does not release real execution ownership");
        }
        _worker.SetResult(Executed());
        await _admission.WaitUntilIdle();
        Assert.That(Resume().Ready.ValidatedThrough, Is.EqualTo(10), "a timed-out result cannot advance the detached session");
    }

    [TestCase(false, TestName = "Run_ReadDeadlineExpires_ClosesIdleStream")]
    [TestCase(true, TestName = "Run_WriteDeadlineExpires_ClosesBlockedStream")]
    public async Task Run_TransportDeadlineExpires_ClosesWithoutExecution(bool write)
    {
        ManualTime time = new();
        _transport = Transport(Execute, time: time);
        if (write) _output.OnWrite = _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        else _input.AddAsync(() => new TaskCompletionSource<ClientFrame?>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        Task stream = Run();
        time.Advance(TimeSpan.FromMinutes(1));
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => stream))!;
        Assert.That((exception.StatusCode, _executions), Is.EqualTo((StatusCode.DeadlineExceeded, 0)), "each transport wait has a finite deadline");
    }

    private Task Run() => _transport.Run(_input, _output, "composer", CancellationToken.None);

    private StreamingTransport Transport(Func<SessionOperation, CancellationToken, Task<ExecutedStreamingBlock>> execute, StreamingTransportLimits? limits = null, TimeProvider? time = null, Func<SessionOperation, CancellationToken, Task<Proof>>? finalize = null) =>
        new(_registry, _admission, execute, limits ?? new StreamingTransportLimits(2, 1000, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            1, NullLogger<StreamingTransport>.Instance, time, finalize);

    private Task<ExecutedStreamingBlock> Execute(SessionOperation operation, CancellationToken cancellation)
    {
        _executions++;
        _workerCancellation = cancellation;
        return _worker.Task;
    }

    private ClientFrame BlockFrame() => Bound(2, new StreamBlock
    {
        Data = new BlockWitness
        {
            Number = 11,
            Hash = Hash(11),
            ParentHash = Hash(1),
            Rlp = Hash(11),
            Witness = new ExecutionWitness(),
        }
    });

    private ClientFrame Bound(ulong requestId, object? operation)
    {
        ServerFrame attachment = _output.Frames[^1];
        ClientFrame frame = new() { SessionId = attachment.SessionId, Epoch = attachment.Epoch, RequestId = requestId };
        switch (operation)
        {
            case StreamBlock block: frame.Block = block; break;
            case Cancel cancel: frame.Cancel = cancel; break;
            case Rewind rewind: frame.Rewind = rewind; break;
            case Finalize finalize: frame.Finalize = finalize; break;
        }
        return frame;
    }

    private ServerFrame Resume() => new StreamingConnection(_registry, "composer", 1).Dispatch(new ClientFrame
    {
        SessionId = _output.Frames[0].SessionId,
        Resume = new Resume(),
        RequestId = 90,
    }).Response!;

    private static Begin Anchor() => new() { RollupId = 1, AnchorNumber = 10, AnchorHash = Hash(1), AnchorStateRoot = Hash(2) };
    private static ExecutedStreamingBlock Executed() => new(11, Hash(11), Hash(1), Hash(2), Hash(31));
    private static ByteString Hash(byte value)
    {
        byte[] bytes = new byte[32];
        bytes.AsSpan().Fill(value);
        return ByteString.CopyFrom(bytes);
    }

    internal sealed class Reader : IAsyncStreamReader<ClientFrame>
    {
        private readonly Queue<Func<Task<ClientFrame?>>> _frames = new();
        public ClientFrame Current { get; private set; } = null!;
        internal int Reads { get; private set; }
        internal void Add(Func<ClientFrame?> frame) => _frames.Enqueue(() => Task.FromResult(frame()));
        internal void AddAsync(Func<Task<ClientFrame?>> frame) => _frames.Enqueue(frame);
        public async Task<bool> MoveNext(CancellationToken cancellation)
        {
            Reads++;
            cancellation.ThrowIfCancellationRequested();
            ClientFrame? frame = _frames.Count == 0 ? null : await _frames.Dequeue()().WaitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (frame is null) return false;
            Current = frame;
            return true;
        }
    }

    internal sealed class Writer : IServerStreamWriter<ServerFrame>
    {
        internal List<ServerFrame> Frames { get; } = [];
        internal Func<ServerFrame, Task> OnWrite { get; set; } = _ => Task.CompletedTask;
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerFrame frame) => WriteAsync(frame, CancellationToken.None);
        public async Task WriteAsync(ServerFrame frame, CancellationToken cancellation)
        {
            await OnWrite(frame).WaitAsync(cancellation).ConfigureAwait(false);
            Frames.Add(frame.Clone());
        }
    }
    internal sealed class ManualTime : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                ManualTimer timer = new(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        internal void Advance(TimeSpan elapsed)
        {
            ManualTimer[] timers;
            lock (_lock) { _ticks += elapsed.Ticks; timers = _timers.ToArray(); }
            foreach (ManualTimer timer in timers) timer.Fire();
        }

        internal sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (time._lock)
                {
                    if (_disposed) return false;
                    _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : time._ticks + dueTime.Ticks;
                    return true;
                }
            }
            internal void Fire()
            {
                lock (time._lock)
                {
                    if (_disposed || _due > time._ticks) return;
                    _due = long.MaxValue;
                }
                callback(state);
            }
            public void Dispose() { lock (time._lock) _disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

}
