// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Abi;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Stateless.Execution.IO;
using Nethermind.StatelessInputGen;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class StatelessInputGeneratorTests
{
    [Test]
    public async Task Raw_block_input_recovers_requests_and_public_keys([Values] bool amsterdam)
    {
        (Block block, Witness witness, ISpecProvider specProvider) = CreateBlock(amsterdam);
        using (witness)
        {
            byte[][] expectedRequests = block.ExecutionRequests!;
            Block rawBlock = Rlp.Decode<Block>(Rlp.Encode(block))!;
            Assert.That(rawBlock.ExecutionRequests, Is.Null);

            Block restored;
            SszPublicKey[] publicKeys;
            byte[]? encoded = await InputGenerator.EncodeInput(rawBlock, witness, specProvider);
            Assert.That(encoded, Is.Not.Null);
            if (amsterdam)
            {
                StatelessInput<SszExecutionPayloadAmsterdam>.Decode(encoded.AsSpan(sizeof(ushort)), out StatelessInput<SszExecutionPayloadAmsterdam> input);
                restored = input.NewPayloadRequest.ToBlock(requestsEnabled: true)!;
                publicKeys = input.PublicKeys;
            }
            else
            {
                StatelessInput<SszExecutionPayload>.Decode(encoded.AsSpan(sizeof(ushort)), out StatelessInput<SszExecutionPayload> input);
                restored = input.NewPayloadRequest.ToBlock(requestsEnabled: true)!;
                publicKeys = input.PublicKeys;
            }

            Assert.That(publicKeys, Has.Length.EqualTo(block.Transactions.Length));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rawBlock.ExecutionRequests, Is.EqualTo(expectedRequests));
                Assert.That(restored.Header.RequestsHash, Is.EqualTo(block.Header.RequestsHash));
                Assert.That(restored.Header.CalculateHash(), Is.EqualTo(block.Hash));
                Assert.That(publicKeys[0].AsSpan().ToArray(), Is.EqualTo(TestItem.PrivateKeyA.PublicKey.PrefixedBytes));
            }
        }
    }

    [Test]
    public async Task Empty_or_pre_request_block_needs_no_replay([Values] bool requestsEnabled)
    {
        Block block = Build.A.Block.WithParentBeaconBlockRoot(TestItem.KeccakA).WithWithdrawals([]).TestObject;
        block.Header.RequestsHash = requestsEnabled ? ExecutionRequestExtensions.EmptyRequestsHash : null;
        using Witness witness = EmptyWitness();

        byte[]? encoded = await InputGenerator.EncodeInput(block, witness,
            new TestSpecProvider(requestsEnabled ? Osaka.Instance : Cancun.Instance));
        Assert.That(encoded, Is.Not.Null);
        StatelessInput<SszExecutionPayload>.Decode(encoded.AsSpan(sizeof(ushort)), out StatelessInput<SszExecutionPayload> input);

        Assert.That(input.NewPayloadRequest.ToBlock(requestsEnabled)!.Header.RequestsHash, Is.EqualTo(block.Header.RequestsHash));
    }

    [Test]
    public void Recovery_rejects_missing_or_unrelated_parent([Values] bool unrelatedParent)
    {
        Block block = Build.A.Block.WithParentBeaconBlockRoot(TestItem.KeccakA).TestObject;
        block.Header.RequestsHash = TestItem.KeccakB;
        using Witness witness = EmptyWitness(unrelatedParent ? [Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes] : []);

        Assert.That(async () => await InputGenerator.EncodeInput(block, witness,
            new TestSpecProvider(Osaka.Instance)), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Recovery_rejects_a_mismatched_requests_hash()
    {
        (Block block, Witness witness, ISpecProvider specProvider) = CreateBlock(amsterdam: false);
        using (witness)
        {
            Block rawBlock = Rlp.Decode<Block>(Rlp.Encode(block))!;
            rawBlock.Header.RequestsHash = TestItem.KeccakB;
            rawBlock.Header.Hash = rawBlock.Header.CalculateHash();

            Assert.That(async () => await InputGenerator.EncodeInput(rawBlock, witness, specProvider),
                Throws.TypeOf<InvalidBlockException>());
        }
    }

    [Test]
    public void Default_stateless_replay_preserves_supplied_requests_hash([Values] bool amsterdam)
    {
        (Block block, Witness witness, ISpecProvider specProvider) = CreateBlock(amsterdam);
        using (witness)
        {
            block.ExecutionRequests = null;
            block.Header.RequestsHash = TestItem.KeccakB;
            block.Header.Hash = block.Header.CalculateHash();
            StatelessBlockProcessingEnv env = new(witness, specProvider, Always.Valid, NullLogManager.Instance);
            using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();
            using IDisposable scope = env.WorldState.BeginScope(headers[^1]);

            (Block processed, _) = env.BlockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain,
                NullBlockTracer.Instance, specProvider.GetSpec(block.Header));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(processed.Header.RequestsHash, Is.EqualTo(TestItem.KeccakB));
                Assert.That(processed.ExecutionRequests, Is.Null);
            }
        }
    }

    [Test]
    public void Recovery_observes_cancellation()
    {
        Block block = Build.A.Block.WithParentBeaconBlockRoot(TestItem.KeccakA).TestObject;
        block.Header.RequestsHash = TestItem.KeccakB;
        using Witness witness = EmptyWitness();

        Assert.That(async () => await InputGenerator.EncodeInput(block, witness,
            new TestSpecProvider(Osaka.Instance), new CancellationToken(canceled: true)), Throws.InstanceOf<OperationCanceledException>());
    }

    private static (Block Block, Witness Witness, ISpecProvider SpecProvider) CreateBlock(bool amsterdam)
    {
        IReleaseSpec spec = amsterdam ? Amsterdam.Instance : Osaka.Instance;
        ISpecProvider specProvider = new TestSpecProvider(spec);
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(spec))
            .AddSingleton<IPruningConfig>(new PruningConfig { Mode = PruningMode.None })
            .Build();
        IWorldStateScopeProvider scopeProvider = container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ILifetimeScope processingScope = container.BeginLifetimeScope(builder =>
            builder.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned());
        IWorldState state = processingScope.Resolve<IWorldState>();
        BlockHeader parent;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether);
            byte[] depositLog = AbiEncoder.Instance.Encode(AbiEncodingStyle.None,
                ExecutionRequestsProcessor.DepositEventAbi, TestItem.ExecutionRequestA.RequestDataParts!);
            AddCode(spec.DepositContractAddress!, Prepare.EvmCode.StoreDataInMemory(0, depositLog)
                .Log(depositLog.Length, 0, [ExecutionRequestsProcessor.DepositEventAbi.Hash])
                .Op(Instruction.PREVRANDAO).PushData(0).Op(Instruction.SSTORE).Done);
            AddRequestsCode(spec.Eip7002ContractAddress!, TestItem.ExecutionRequestD.RequestData!);
            AddRequestsCode(spec.Eip7251ContractAddress!, TestItem.ExecutionRequestG.RequestData!);
            if (amsterdam)
            {
                AddRequestsCode(Eip8282Constants.BuilderDepositRequestPredeployAddress, new byte[ExecutionRequestExtensions.BuilderDepositRequestsBytesSize]);
                AddRequestsCode(Eip8282Constants.BuilderExitRequestPredeployAddress, new byte[ExecutionRequestExtensions.BuilderExitRequestsBytesSize]);
            }
            state.Commit(spec);
            state.CommitTree(0);
            parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(state.StateRoot).TestObject;
        }

        using TrieStore.StableLockScope stableState = container.Resolve<MainPruningTrieStoreFactory>().PruningTrieStore.PrepareStableState(CancellationToken.None);
        IDbProvider dbProvider = container.Resolve<IDbProvider>();
        Witness witness = new()
        {
            Codes = new ArrayPoolList<byte[]>([.. dbProvider.CodeDb.GetAllValues()]),
            State = new ArrayPoolList<byte[]>([.. dbProvider.StateDb.GetAllValues()]),
            Keys = ArrayPoolList<byte[]>.Empty(),
            Headers = new ArrayPoolList<byte[]>([Rlp.Encode(parent).Bytes])
        };
        try
        {
            Transaction tx = Build.A.Transaction.WithTo(spec.DepositContractAddress!).WithGasLimit(1_000_000)
                .WithGasPrice(1).SignedAndResolved(new EthereumEcdsa(specProvider.ChainId), TestItem.PrivateKeyA).TestObject;
            Block suggested = Build.A.Block.WithParent(parent).WithPostMergeRules()
                .WithTimestamp(parent.Timestamp + 12).WithBaseFeePerGas(0).WithBlobGasUsed(0).WithExcessBlobGas(0)
                .WithSlotNumber(amsterdam ? 1UL : null)
                .WithParentBeaconBlockRoot(TestItem.KeccakA).WithWithdrawals([]).WithTransactions(tx).TestObject;
            StatelessBlockProcessingEnv env = new(witness, specProvider, Always.Valid, NullLogManager.Instance)
            {
                ExecutionRequestsProcessorFactory = ExecutionRequestsProcessorFactory.Instance
            };
            using IDisposable scope = env.WorldState.BeginScope(parent);
            (Block block, _) = env.BlockProcessor.ProcessOne(suggested, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, spec);
            block.DisposeAccountChanges();
            Assert.That(block.ExecutionRequests, Has.Length.EqualTo(amsterdam ? 5 : 3));
            return (block, witness, specProvider);
        }
        catch
        {
            witness.Dispose();
            throw;
        }

        void AddRequestsCode(Address address, byte[] requests) =>
            AddCode(address, Prepare.EvmCode.StoreDataInMemory(0, requests).Return(requests.Length, 0).Done);

        void AddCode(Address address, byte[] code)
        {
            state.CreateAccount(address, 0);
            state.InsertCode(address, Keccak.Compute(code), code, spec);
        }
    }

    private static Witness EmptyWitness(byte[][]? headers = null) => new()
    {
        Codes = ArrayPoolList<byte[]>.Empty(),
        State = ArrayPoolList<byte[]>.Empty(),
        Keys = ArrayPoolList<byte[]>.Empty(),
        Headers = new ArrayPoolList<byte[]>(headers ?? [])
    };
}
