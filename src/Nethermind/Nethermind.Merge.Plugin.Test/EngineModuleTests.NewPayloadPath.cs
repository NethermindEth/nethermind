// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task NewPayloadV3_checks_parameters_inside_the_preparation_the_handler_builds_the_block_with()
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] transactions, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Cancun.Instance, 1);
        using MergeTestBlockchain disposeChain = chain;
        ExecutionPayloadV3 produced = (await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId!))).Data!.ExecutionPayload;
        GroupTrackingPayloadV3 payload = GroupTrackingPayloadV3.Create(produced.TryGetBlock().Data!);
        // Encoded only, so the parameter checks decode them as they do for a payload read off the wire.
        payload.Transactions = produced.Transactions;
        Hash256[] blobVersionedHashes = transactions.SelectMany(static tx => tx.BlobVersionedHashes ?? []).Select(static h => new Hash256(h!)).ToArray();

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV3(payload, blobVersionedHashes, payload.ParentBeaconBlockRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(payload.CheckingGroup, Is.Not.Null, "the parameter checks ran outside a preparation");
            Assert.That(payload.BuildingGroup, Is.SameAs(payload.CheckingGroup), "the handler built the block in a preparation of its own");
        }
    }

    [Test]
    public async Task NewPayload_answers_without_waiting_for_its_received_line()
    {
        ReceivedLineLogger receivedLogger = new(hold: true);
        using MergeTestBlockchain chain = await CreateBlockchainLoggingNewPayloadTo(receivedLogger);
        ExecutionPayload payload = await CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);

        // Off the test thread: a request that writes the line itself stays inside the logger until it is released.
        Task<ResultWrapper<PayloadStatusV1>> newPayload = Task.Run(() => chain.EngineRpcModule.engine_newPayloadV1(payload));
        Task first = await Task.WhenAny(newPayload, Task.Delay(TimeSpan.FromSeconds(10)));
        receivedLogger.Release();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.SameAs(newPayload), "newPayload waited for its received line to be written");
            Assert.That((await newPayload).Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(await receivedLogger.Line.WaitAsync(TimeSpan.FromSeconds(10)), Does.StartWith("Received New Block:  "));
        }
    }

    [Test]
    public async Task NewPayload_logs_its_received_line_once_with_the_payload_details()
    {
        ReceivedLineLogger receivedLogger = new();
        using MergeTestBlockchain chain = await CreateBlockchainLoggingNewPayloadTo(receivedLogger);
        ExecutionPayload payload = await CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);
        string extraData = payload.TryGetBlock().Data!.ParsedExtraData();

        ResultWrapper<PayloadStatusV1> result = await chain.EngineRpcModule.engine_newPayloadV1(payload);
        string line = await receivedLogger.Line.WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(line, Does.StartWith($"Received New Block:  {payload.BlockNumber} ({payload.BlockHash.ToShortString()})      | limit {payload.GasLimit,13:N0} "));
            Assert.That(line, Does.EndWith($" | {extraData}"));
            Assert.That(receivedLogger.Lines, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task NewPayload_is_answered_when_writing_its_received_line_fails()
    {
        ReceivedLineLogger receivedLogger = new(fail: true);
        using MergeTestBlockchain chain = await CreateBlockchainLoggingNewPayloadTo(receivedLogger);
        ExecutionPayload payload = await CreateBlockRequest(chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD);

        ResultWrapper<PayloadStatusV1> result = await chain.EngineRpcModule.engine_newPayloadV1(payload);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data?.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(await receivedLogger.LoggedError.WaitAsync(TimeSpan.FromSeconds(10)), Is.SameAs(receivedLogger.Failure));
        }
    }

    private async Task<MergeTestBlockchain> CreateBlockchainLoggingNewPayloadTo(InterfaceLogger logger)
    {
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<NewPayloadHandler>().Returns(new ILogger(logger));
        return await CreateBlockchain(configurer: builder => builder.AddSingleton<ILogManager>(logManager));
    }

    /// <summary>Takes the "Received New Block" lines, optionally holding them until released or failing them.</summary>
    private sealed class ReceivedLineLogger(bool hold = false, bool fail = false) : InterfaceLogger
    {
        private readonly ManualResetEventSlim _released = new(!hold);
        private readonly TaskCompletionSource<string> _line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception> _error = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _lines;

        public Exception Failure { get; } = new InvalidOperationException("The log target failed.");
        public Task<string> Line => _line.Task;
        public Task<Exception> LoggedError => _error.Task;
        public int Lines => Volatile.Read(ref _lines);
        public void Release() => _released.Set();

        public void Info(string text)
        {
            if (!text.StartsWith("Received New Block", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref _lines);
            // Bounded, so a request that does wait here still finishes once the test has seen it wait.
            _released.Wait(TimeSpan.FromSeconds(30));
            if (fail) throw Failure;
            _line.TrySetResult(text);
        }

        public void Error(string text, Exception? ex = null)
        {
            if (ex is not null) _error.TrySetResult(ex);
        }

        public void Warn(string text) { }
        public void Debug(string text) { }
        public void Trace(string text) { }
        public bool IsInfo => true;
        public bool IsWarn => false;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public bool IsError => true;
    }

    /// <summary>Records the worker group current while the parameter checks read the payload and while the handler builds the block.</summary>
    private sealed class GroupTrackingPayloadV3 : ExecutionPayloadV3
    {
        public ParallelUnbalancedWork.WorkerGroup? CheckingGroup { get; private set; }
        public ParallelUnbalancedWork.WorkerGroup? BuildingGroup { get; private set; }

        public new static GroupTrackingPayloadV3 Create(Block block) => Create<GroupTrackingPayloadV3>(block);

        // Of everything engine_newPayloadV3 reaches, only the parameter checks read this.
        public override byte[]? BlockAccessList
        {
            get
            {
                CheckingGroup ??= ParallelUnbalancedWork.GetCurrentGroup();
                return base.BlockAccessList;
            }
            set => base.BlockAccessList = value;
        }

        public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
        {
            BuildingGroup ??= ParallelUnbalancedWork.GetCurrentGroup();
            return base.TryGetBlock(totalDifficulty);
        }
    }
}
