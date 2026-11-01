// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.Flat.History.Prestate;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>
/// Traces blocks below the persisted flat state, where every state read goes to flat history, with and without a
/// pre-state record, and requires the responses to be byte for byte the same.
/// </summary>
/// <remarks>
/// Scenario:
/// 1. A flat-history chain is seeded at genesis like a node that captured from there.
/// 2. A setup block deploys a counter contract.
/// 3. The traced block calls the counter twice, creates a contract that writes storage in its constructor, delegates
///    an account to the counter with an EIP-7702 authorization and calls it, and sends value to a fresh account.
/// 4. One more block follows, then the in-memory state is flushed, so both blocks read their parent from history.
/// </remarks>
public class PrestateRecordParityTests
{
    private static readonly object CallTracer = new { tracer = "callTracer" };
    private static readonly object PrestateTracer = new { tracer = "prestateTracer" };

    [Test]
    public async Task Trace_FromARecord_MatchesTheHistoryPathByteForByte()
    {
        using Chain chain = await Chain.Create();
        ulong traced = chain.Traced.Number;

        string[] history = await chain.TraceAll(traced);
        Assert.That(chain.Cache.LeaseHits, Is.Zero, "precondition: no record exists yet, so the traces read history");

        long invalidBefore = Nethermind.State.Flat.Metrics.PrestateRecordsInvalid;
        chain.WriteRawRecord(chain.Traced, [1, 2, 3]);
        string[] withCorruptRecord = await chain.TraceAll(traced);
        long hitsAfterCorrupt = chain.Cache.LeaseHits;
        long invalidAfter = Nethermind.State.Flat.Metrics.PrestateRecordsInvalid;

        Assert.That(chain.TryRecord(chain.Traced), Is.True, "precondition: the builder capture records the block");
        string[] replayedFromRecord = await chain.TraceAll(traced);
        long hitsAfterRecord = chain.Cache.LeaseHits;

        chain.Index(chain.Traced);
        string[] parallelFromRecord = await chain.TraceAll(traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(invalidAfter, Is.GreaterThan(invalidBefore), "the malformed record must actually be read and refused");
            Assert.That(hitsAfterCorrupt, Is.Zero, "a malformed record is never used");
            Assert.That(withCorruptRecord, Is.EqualTo(history), "a malformed record falls back to history");
            Assert.That(hitsAfterRecord, Is.GreaterThan(0L), "the traces must actually read the record for the comparison to mean anything");
            Assert.That(replayedFromRecord, Is.EqualTo(history), "a sequential replay from the record answers like history");
            Assert.That(parallelFromRecord, Is.EqualTo(history), "a seeded parallel trace from the record answers like history");
        }
    }

    /// <summary>The record must really be read on the trace path: a changed balance in it shows in the prestate trace.</summary>
    [Test]
    public async Task Trace_FromATamperedRecord_Differs()
    {
        using Chain chain = await Chain.Create();
        Block setup = chain.Setup;
        string history = await chain.Trace("debug_traceBlockByNumber", setup.Number, PrestateTracer);
        Assert.That(chain.TryRecord(setup), Is.True, "precondition: the builder capture records the block");
        byte[] record = chain.ReadRawRecord(setup);

        chain.WriteRawRecord(setup, TamperBalance(record, TestItem.AddressB));
        string tampered = await chain.Trace("debug_traceBlockByNumber", setup.Number, PrestateTracer);

        Assert.That(tampered, Is.Not.EqualTo(history), "a trace that ignored the record could not see the changed value");
    }

    /// <summary>Changes the first byte of the account's balance; the value stays minimal, so the record stays valid.</summary>
    private static byte[] TamperBalance(byte[] record, Address address)
    {
        int account = record.AsSpan().IndexOf(address.Bytes);
        Assert.That(account, Is.GreaterThan(0), "precondition: the sender is in the record");
        int position = account + Address.Size + 1;
        while ((record[position] & 0x80) != 0) position++;
        position++;
        Assert.That(record[position], Is.GreaterThan((byte)1), "precondition: the sender has a multi-byte balance");
        byte[] tampered = (byte[])record.Clone();
        tampered[position + 1] ^= 0x01;
        if (tampered[position + 1] == 0) tampered[position + 1] = 0x02;
        return tampered;
    }

    private sealed class Chain : IDisposable
    {
        private readonly TestRpcBlockchain _blockchain;
        private readonly IDb _records;

        private Chain(TestRpcBlockchain blockchain, Block setup, Block traced)
        {
            _blockchain = blockchain;
            Setup = setup;
            Traced = traced;
            Cache = blockchain.Container.Resolve<PrestateRecordCache>();
            _records = blockchain.Container.ResolveKeyed<IDb>(DbNames.PrestateRecords);
        }

        public Block Setup { get; }

        public Block Traced { get; }

        public PrestateRecordCache Cache { get; }

        public static async Task<Chain> Create()
        {
            TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(new FlatHistoryRpcBlockchain { SealEngineType = Nethermind.Core.SealEngineType.NethDev })
                .Build(builder => builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false }));
            SeedHistory(blockchain);

            ulong nonce = blockchain.ReadOnlyState.GetNonce(TestItem.AddressB);
            byte[] counter = Prepare.EvmCode
                .PushData(0).Op(Instruction.SLOAD).PushData(1).Op(Instruction.ADD).PushData(0).Op(Instruction.SSTORE)
                .PushData(TestItem.AddressE).Op(Instruction.BALANCE).Op(Instruction.POP)
                .Op(Instruction.STOP).Done;
            Address counterAddress = ContractAddress.From(TestItem.AddressB, nonce);
            Block setup = await blockchain.AddBlock(Deploy(counter, nonce++));

            byte[] constructorWrites = Prepare.EvmCode.PushData(0x2a).PushData(1).Op(Instruction.SSTORE).ForInitOf(counter).Done;
            EthereumEcdsa ecdsa = new(blockchain.SpecProvider.ChainId);
            AuthorizationTuple delegation = ecdsa.Sign(TestItem.PrivateKeyD, blockchain.SpecProvider.ChainId, counterAddress,
                blockchain.ReadOnlyState.GetNonce(TestItem.AddressD));
            Block traced = await blockchain.AddBlock(
                Call(counterAddress, nonce++),
                Call(counterAddress, nonce++),
                Deploy(constructorWrites, nonce++),
                Build.A.Transaction.WithType(TxType.SetCode).WithTo(TestItem.AddressD).WithNonce(nonce++).WithGasLimit(200_000)
                    .WithMaxFeePerGas(1_000_000_000).WithMaxPriorityFeePerGas(1).WithChainId(blockchain.SpecProvider.ChainId)
                    .WithAuthorizationCode(delegation).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
                Build.A.Transaction.WithTo(TestItem.AddressF).WithValue(5).WithNonce(nonce++).WithGasLimit(50_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject);
            Assert.That(traced.Transactions, Has.Length.EqualTo(5), "precondition: every transaction is included");
            await blockchain.AddBlock();

            blockchain.WorldStateManager.FlushCache(CancellationToken.None);
            IFlatDbManager flat = blockchain.Container.Resolve<IFlatDbManager>();
            Assert.That(flat.IsHistoricalState(new StateId(blockchain.BlockTree.FindHeader(traced.ParentHash!)!)), Is.True,
                "precondition: the traced block reads its parent from flat history");
            return new Chain(blockchain, setup, traced);
        }

        public async Task<string[]> TraceAll(ulong block) =>
        [
            await Trace("debug_traceBlockByNumber", block, CallTracer),
            await Trace("debug_traceBlockByNumber", block, PrestateTracer),
            await Trace("trace_block", block, null),
        ];

        public Task<string> Trace(string method, ulong block, object? options) => method.StartsWith("trace_")
            ? RpcTest.TestSerializedRequest(_blockchain.TraceRpcModule, method, block)
            : RpcTest.TestSerializedRequest(_blockchain.DebugRpcModule, method, block, options!);

        /// <summary>Records the block the way the transaction changeset builder does: one execution on its parent through
        /// a capture-decorated executor, then the recorder.</summary>
        public bool TryRecord(Block block)
        {
            PrestateRecorder recorder = _blockchain.Container.Resolve<PrestateRecorder>();
            PrestateCapture capture = recorder.CreateCapture();
            using IHistoryBlockExecutor executor = _blockchain.Container.Resolve<IHistoryBlockExecutorFactory>().Create(capture.Decorate);
            capture.Arm();
            bool executed = executor.TryExecute(block.Number, NullBlockTracer.Instance, CancellationToken.None);
            Assert.That(executed, Is.True, "precondition: the block executes on its parent");
            bool written = recorder.TryWrite(capture, block.Number, block.Hash!.ValueHash256, CancellationToken.None);
            Assert.That(written, Is.True, $"precondition: the record is written ({capture.Failure}, parent {capture.Parent})");
            return written;
        }

        public void Index(Block block)
        {
            TransactionChangesetIndex index = _blockchain.Container.Resolve<TransactionChangesetIndex>();
            using IHistoryBlockExecutor executor = _blockchain.Container.Resolve<IHistoryBlockExecutorFactory>().Create();
            using TransactionChangesetIndex.BlockCapture capture = index.StartBlock(block.Number);
            Assert.That(executor.TryExecute(block.Number, capture.Tracer, CancellationToken.None) && capture.Commit()
                && index.TryClaim(block.Number, block.Number), Is.True, "precondition: the block is indexed");
        }

        public byte[] ReadRawRecord(Block block) => _records.Get(Key(block))!;

        /// <summary>Writes through the store, so the cache does not keep trusting an earlier "no record".</summary>
        public void WriteRawRecord(Block block, byte[] value) =>
            _blockchain.Container.Resolve<PrestateRecordStore>().Write(new StateId(_blockchain.BlockTree.FindHeader(block.ParentHash!)!), value);

        private byte[] Key(Block block)
        {
            BlockHeader parent = _blockchain.BlockTree.FindHeader(block.ParentHash!)!;
            byte[] key = new byte[8 + Hash256.Size];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key, parent.Number);
            parent.StateRoot!.Bytes.CopyTo(key.AsSpan(8));
            return key;
        }

        private static void SeedHistory(TestRpcBlockchain blockchain)
        {
            // TestBlockchain runs no init steps: seed the genesis floor and capture from there, as a node does.
            HistoryWriter historyWriter = blockchain.Container.Resolve<HistoryWriter>();
            Hash256 genesisRoot = blockchain.BlockTree.Genesis!.StateRoot!;
            historyWriter.SeedGenesis([], genesisRoot);
            historyWriter.CaptureUpTo(new StateId(0, genesisRoot), blockchain.Container.Resolve<ISnapshotRepository>(), CancellationToken.None);
        }

        private static Transaction Deploy(byte[] initCode, ulong nonce) =>
            Build.A.Transaction.WithCode(initCode).WithTo(null).WithNonce(nonce).WithGasLimit(500_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        private static Transaction Call(Address to, ulong nonce) =>
            Build.A.Transaction.WithTo(to).WithNonce(nonce).WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        public void Dispose() => _blockchain.Dispose();
    }

    private sealed class FlatHistoryRpcBlockchain : TestRpcBlockchain
    {
        protected override IEnumerable<IConfig> CreateConfigs() =>
        [
            .. base.CreateConfigs(),
            new FlatDbConfig
            {
                Enabled = true,
                HistoryEnabled = true,
                HistoryTransactionIndexEnabled = true,
                HistoryPrestateRecordsEnabled = true,
                HistoryPrestateRecordsLazyOpenReads = 0
            }
        ];
    }
}
