// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Tracing;
using Nethermind.Core.Container;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Overlay;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Overlay;

public class OverlayRpcModuleTests
{
    private static readonly Hash256 TopicA = Keccak.Compute("A");
    private static readonly Hash256 TopicB = Keccak.Compute("B");

    private static readonly byte[] ContextConstructor = Prepare.EvmCode
        .Op(Instruction.ADDRESS).PushData(0).Op(Instruction.MSTORE)
        .Op(Instruction.CALLER).PushData(32).Op(Instruction.MSTORE)
        .PushData(64).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] NestedCreateConstructor = Prepare.EvmCode
        .PushData(0).PushData(0).PushData(0).Op(Instruction.CREATE)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(20).PushData(12).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] CallValueConstructor = Prepare.EvmCode
        .Op(Instruction.CALLVALUE).PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    [Test]
    public async Task CallConstructor_returns_runtime_code_of_new_creation_code()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        byte[] runtime = Emitter(TopicA, TopicB);

        using JsonDocument response = await Request(chain, "overlay_callConstructor", deployment.Contract, Prepare.EvmCode.ForInitOf(runtime).Done);

        Assert.That(Code(response), Is.EqualTo(runtime));
    }

    [Test]
    public async Task CallConstructor_runs_in_the_context_of_the_deployment([Values] bool byFactory)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        (Address contract, Address creator) = byFactory
            ? (await DeployByFactory(chain, deployment.Factory), deployment.Factory)
            : (deployment.Contract, TestItem.AddressA);

        using JsonDocument response = await Request(chain, "overlay_callConstructor", contract, ContextConstructor);

        byte[] expected = new byte[64];
        contract.Bytes.CopyTo(expected.AsSpan(12));
        creator.Bytes.CopyTo(expected.AsSpan(44));
        Assert.That(Code(response), Is.EqualTo(expected));
    }

    [Test]
    public async Task CallConstructor_gives_nested_creations_the_addresses_of_the_deployment([Values] bool byFactory)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        Address contract = byFactory ? await DeployByFactory(chain, deployment.Factory) : deployment.Contract;

        using JsonDocument response = await Request(chain, "overlay_callConstructor", contract, NestedCreateConstructor);

        Assert.That(Code(response), Is.EqualTo(ContractAddress.From(contract, 1).Bytes.ToArray()));
    }

    [Test]
    public async Task CallConstructor_sends_the_value_that_the_factory_gets_in_the_same_transaction()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Address factory = ContractAddress.From(TestItem.AddressA, nonce);
        byte[] factoryRuntime = Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(Emitter(TopicA)).Done, 1).STOP().Done;
        await chain.AddBlock(Build.A.Transaction.WithCode(Prepare.EvmCode.ForInitOf(factoryRuntime).Done).WithValue(0).WithNonce(nonce)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Address child = ContractAddress.From(factory, chain.ReadOnlyState.GetNonce(factory));
        await chain.AddBlock(Build.A.Transaction.WithTo(factory).WithValue(1).WithNonce(nonce + 1)
            .WithGasLimit(200_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.ReadOnlyState.GetBalance(child), Is.EqualTo(UInt256.One), "precondition: the factory sent the value to the child");
            Assert.That(chain.ReadOnlyState.GetBalance(factory), Is.EqualTo(UInt256.Zero), "precondition: the factory had no balance of its own");
        }

        using JsonDocument response = await Request(chain, "overlay_callConstructor", child, CallValueConstructor);

        Assert.That(new UInt256(Code(response), isBigEndian: true), Is.EqualTo(UInt256.One));
    }

    [Test]
    public async Task CallConstructor_reads_only_state_from_the_creation_block_parent()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        for (int i = 0; i < 8; i++)
        {
            await chain.AddBlock();
        }

        Deployment deployment = await Deploy(chain);
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.HasStateForBlock(Arg.Any<BlockHeader?>()).Returns(c => c.ArgAt<BlockHeader?>(0) is { } header && header.Number + 1 >= deployment.First);
        stateReader.TryGetAccount(Arg.Any<BlockHeader?>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(c =>
        {
            bool found = chain.StateReader.TryGetAccount(c.ArgAt<BlockHeader?>(0), c.ArgAt<Address>(1), out AccountStruct account);
            c[2] = account;
            return found;
        });
        await using ILifetimeScope scope = chain.Container.BeginLifetimeScope(b => b.AddSingleton(stateReader));
        OverlayModuleFactory factory = new(scope, scope.Resolve<IOverridableEnvFactory>(), scope.Resolve<IReadOnlyList<IBlockValidationModule>>());

        using JsonDocument response = JsonDocument.Parse(await RpcTest.TestSerializedRequest(factory.Create(), "overlay_callConstructor", deployment.Contract, ContextConstructor));

        Assert.That(Code(response)[12..32], Is.EqualTo(deployment.Contract.Bytes.ToArray()));
    }

    [Test]
    public async Task CallConstructor_fails_without_contract()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        using JsonDocument response = await Request(chain, "overlay_callConstructor", TestItem.AddressF, ContextConstructor);

        Assert.That(ErrorCode(response), Is.EqualTo(ErrorCodes.ResourceNotFound));
    }

    [Test]
    public async Task GetLogs_without_overrides_matches_eth_getLogs([Values] bool filterByAddress, [Values] bool eip8116)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev)
            .Build(new TestSpecProvider(new OverridableReleaseSpec(Osaka.Instance) { IsEip8116Enabled = eip8116 }));
        Deployment deployment = await Deploy(chain);
        object filter = filterByAddress
            ? new { fromBlock = deployment.First.ToHexString(true), toBlock = deployment.Last.ToHexString(true), address = deployment.Contract }
            : new { fromBlock = deployment.First.ToHexString(true), toBlock = deployment.Last.ToHexString(true) };

        using JsonDocument overlay = await Request(chain, "overlay_getLogs", filter);
        using JsonDocument eth = JsonDocument.Parse(await chain.TestEthRpc("eth_getLogs", filter));

        JsonElement result = overlay.RootElement.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetArrayLength(), Is.EqualTo(deployment.Calls));
            Assert.That(result.GetRawText(), Is.EqualTo(eth.RootElement.GetProperty("result").GetRawText()));
            Assert.That(LogIndex(result[deployment.Calls - 1]), Is.EqualTo(eip8116 ? 0 : 1), "the second call in the last block");
        }
    }

    [Test]
    public async Task GetLogs_by_block_hash_replays_that_block_when_it_is_not_canonical()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        Block sibling = AddSiblingOfHead(chain);

        using JsonDocument response = await Request(chain, "overlay_getLogs", new { blockHash = sibling.Hash }, Override(deployment.Contract, Emitter(TopicA, TopicB)));

        JsonElement[] logs = [.. response.RootElement.GetProperty("result").EnumerateArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.IsMainChain(sibling.Header), Is.False);
            Assert.That(logs, Has.Length.EqualTo(2));
            Assert.That(logs.Select(static l => l.GetProperty("blockHash").GetString()), Is.All.EqualTo(sibling.Hash!.ToString()));
        }
    }

    [Test]
    public async Task GetLogs_fails_for_a_range_with_a_block_that_is_not_canonical([Values] bool toByHash)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        Block sibling = AddSiblingOfHead(chain);

        // By hash, both ends are at the same height but select two different blocks, so the range is not one block.
        using JsonDocument response = await Request(chain, "overlay_getLogs", toByHash
            ? new { fromBlock = sibling.Hash!.ToString(), toBlock = chain.BlockTree.Head!.Hash!.ToString() }
            : new { fromBlock = sibling.Hash!.ToString(), toBlock = deployment.Last.ToHexString(true) });

        Assert.That(ErrorCode(response), Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public async Task GetLogs_accepts_a_canonical_block_hash_as_a_range_end()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);

        using JsonDocument response = await Request(chain, "overlay_getLogs",
            new { fromBlock = chain.BlockTree.FindHeader(deployment.First)!.Hash!.ToString(), toBlock = deployment.Last.ToHexString(true) });

        Assert.That(response.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(deployment.Calls));
    }

    [Test]
    public async Task GetLogs_fails_for_pending([Values] bool from)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();

        using JsonDocument response = await Request(chain, "overlay_getLogs",
            from ? new { fromBlock = "pending", toBlock = "pending" } : new { fromBlock = "earliest", toBlock = "pending" });

        Assert.That(ErrorCode(response), Is.EqualTo(ErrorCodes.InvalidInput));
    }

    [Test]
    public async Task GetLogs_returns_logs_of_overridden_code()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);

        using JsonDocument response = await Request(chain, "overlay_getLogs",
            new { fromBlock = deployment.First.ToHexString(true), toBlock = deployment.Last.ToHexString(true) },
            Override(deployment.Contract, Emitter(TopicA, TopicB)));
        using JsonDocument original = JsonDocument.Parse(await chain.TestEthRpc("eth_getLogs",
            new { fromBlock = deployment.First.ToHexString(true), toBlock = deployment.Last.ToHexString(true) }));

        JsonElement[] logs = [.. response.RootElement.GetProperty("result").EnumerateArray()];
        JsonElement[] originalLogs = [.. original.RootElement.GetProperty("result").EnumerateArray()];
        Assert.That(logs, Has.Length.EqualTo(2 * deployment.Calls));
        for (int i = 0; i < originalLogs.Length; i++)
        {
            JsonElement a = logs[2 * i];
            JsonElement b = logs[2 * i + 1];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(a.GetProperty("topics")[0].GetString(), Is.EqualTo(TopicA.ToString()));
                Assert.That(a.GetProperty("transactionHash").GetString(), Is.EqualTo(originalLogs[i].GetProperty("transactionHash").GetString()));
                Assert.That(b.GetProperty("topics")[0].GetString(), Is.EqualTo(TopicB.ToString()));
                Assert.That(b.GetProperty("address").GetString(), Is.EqualTo(deployment.Contract.ToString()));
                Assert.That(b.GetProperty("transactionHash").GetString(), Is.EqualTo(a.GetProperty("transactionHash").GetString()));
                Assert.That(b.GetProperty("blockHash").GetString(), Is.EqualTo(a.GetProperty("blockHash").GetString()));
                Assert.That(LogIndex(b), Is.EqualTo(LogIndex(a) + 1));
            }
        }
    }

    [Test]
    public async Task GetLogs_with_a_nonce_override_keeps_the_canonical_transactions()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        Block block = chain.BlockFinder.FindBlock(deployment.Last)!;
        ulong[] nonces = [.. block.Transactions.Select(static tx => tx.Nonce)];
        byte[][] encoded = [.. block.Transactions.Select(static tx => Rlp.Encode(tx).Bytes)];

        using JsonDocument response = await Request(chain, "overlay_getLogs",
            new { fromBlock = deployment.Last.ToHexString(true), toBlock = deployment.Last.ToHexString(true) },
            new Dictionary<string, object> { [TestItem.AddressA.ToString()] = new { nonce = 1000UL.ToHexString(true) } });

        Block reloaded = chain.BlockFinder.FindBlock(deployment.Last)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(2));
            Assert.That(reloaded.Transactions.Select(static tx => tx.Nonce), Is.EqualTo(nonces));
            Assert.That(reloaded.Transactions.Select(static tx => Rlp.Encode(tx).Bytes), Is.EqualTo(encoded));
        }
    }

    [TestCase(false, false, 3)]
    [TestCase(true, false, 3)]
    [TestCase(true, true, 1)]
    [TestCase(false, true, 0)]
    public async Task GetLogs_filters_overridden_logs(bool topicB, bool otherAddress, int expected)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        Address address = otherAddress ? TestItem.AddressF : deployment.Contract;
        Dictionary<string, object> overrides = Override(deployment.Contract, Emitter(TopicA, TopicB));
        overrides[TestItem.AddressF.ToString()] = new { code = Emitter(TopicB).ToHexString(true) };
        await chain.AddBlock(Build.A.Transaction.WithTo(TestItem.AddressF).WithNonce(chain.ReadOnlyState.GetNonce(TestItem.AddressA))
            .WithGasLimit(100_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        using JsonDocument response = await Request(chain, "overlay_getLogs",
            new
            {
                fromBlock = deployment.First.ToHexString(true),
                toBlock = (deployment.Last + 1).ToHexString(true),
                address,
                topics = new[] { new[] { topicB ? TopicB : TopicA } },
            },
            overrides);

        JsonElement[] logs = [.. response.RootElement.GetProperty("result").EnumerateArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs, Has.Length.EqualTo(expected));
            Assert.That(logs.Select(static l => l.GetProperty("address").GetString()), Is.All.EqualTo(address.ToString()));
        }
    }

    [Test]
    public async Task GetLogs_fails_when_range_exceeds_limit([Values(3, 4)] int maxRange)
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        chain.Container.Resolve<IJsonRpcConfig>().OverlayMaxBlockRange = maxRange;

        using JsonDocument response = await Request(chain, "overlay_getLogs",
            new { fromBlock = deployment.First.ToHexString(true), toBlock = deployment.Last.ToHexString(true) });

        ulong blocks = deployment.Last - deployment.First + 1;
        if ((ulong)maxRange < blocks)
        {
            Assert.That(ErrorCode(response), Is.EqualTo(ErrorCodes.LimitExceeded));
        }
        else
        {
            Assert.That(response.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(deployment.Calls));
        }
    }

    [Test]
    public async Task Methods_fail_without_state()
    {
        using TestRpcBlockchain chain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build();
        Deployment deployment = await Deploy(chain);
        OverlayRpcModule module = new(
            Substitute.For<IOverridableEnv<ITracer>>(),
            chain.BlockFinder,
            Substitute.For<IStateReader>(),
            chain.Container.Resolve<FilterStore>(),
            chain.SpecProvider,
            chain.Container.Resolve<IJsonRpcConfig>());

        using JsonDocument logs = JsonDocument.Parse(await RpcTest.TestSerializedRequest<IOverlayRpcModule>(module, "overlay_getLogs",
            new { fromBlock = deployment.First.ToHexString(true), toBlock = deployment.Last.ToHexString(true) }));
        using JsonDocument code = JsonDocument.Parse(await RpcTest.TestSerializedRequest<IOverlayRpcModule>(module, "overlay_callConstructor",
            deployment.Contract, ContextConstructor));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ErrorCode(logs), Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(ErrorCode(code), Is.EqualTo(ErrorCodes.ResourceUnavailable));
        }
    }

    private sealed record Deployment(Address Contract, Address Factory, ulong First, ulong Last, int Calls);

    /// <summary>Deploys an emitter of <see cref="TopicA"/> and the factory, then calls the emitter three times over two blocks with a transfer block between.</summary>
    private static async Task<Deployment> Deploy(TestRpcBlockchain chain)
    {
        ulong nonce = chain.ReadOnlyState.GetNonce(TestItem.AddressA);
        Address contract = ContractAddress.From(TestItem.AddressA, nonce);
        Address factory = ContractAddress.From(TestItem.AddressA, nonce + 1);
        byte[] factoryRuntime = Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(Emitter(TopicA)).Done, UInt256.Zero).STOP().Done;

        Block first = await chain.AddBlock(
            Deploy(nonce, Emitter(TopicA)),
            Deploy(nonce + 1, factoryRuntime));
        await chain.AddBlock(Call(nonce + 2, contract));
        await chain.AddBlock(Build.A.Transaction.WithTo(TestItem.AddressB).WithValue(1).WithNonce(nonce + 3)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Block last = await chain.AddBlock(Call(nonce + 4, contract), Call(nonce + 5, contract));
        Assert.That(last.Transactions, Has.Length.EqualTo(2), "precondition: both calls are in one block");
        return new Deployment(contract, factory, first.Number, last.Number, 3);
    }

    private static async Task<Address> DeployByFactory(TestRpcBlockchain chain, Address factory)
    {
        Address child = ContractAddress.From(factory, chain.ReadOnlyState.GetNonce(factory));
        await chain.AddBlock(Call(chain.ReadOnlyState.GetNonce(TestItem.AddressA), factory));
        Assert.That(chain.ReadOnlyState.GetCode(child).Length, Is.GreaterThan(0), "precondition: the factory deployed the child");
        return child;
    }

    /// <summary>Adds a block at the head height with the first transaction of the head, without making it canonical.</summary>
    private static Block AddSiblingOfHead(TestRpcBlockchain chain)
    {
        Block head = chain.BlockTree.Head!;
        BlockHeader header = head.Header.Clone();
        header.ExtraData = [1];
        header.Hash = header.CalculateHash();
        Block sibling = new(header, new BlockBody([head.Transactions[0]], [], head.Withdrawals));
        Assert.That(chain.BlockTree.SuggestBlock(sibling, BlockTreeSuggestOptions.ForceDontSetAsMain), Is.EqualTo(AddBlockResult.Added));
        return sibling;
    }

    private static Transaction Deploy(ulong nonce, byte[] runtime) =>
        Build.A.Transaction.WithCode(Prepare.EvmCode.ForInitOf(runtime).Done).WithNonce(nonce)
            .WithGasLimit(1_000_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static Transaction Call(ulong nonce, Address to) =>
        Build.A.Transaction.WithTo(to).WithNonce(nonce).WithGasLimit(200_000).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static byte[] Emitter(params Hash256[] topics)
    {
        Prepare code = Prepare.EvmCode;
        foreach (Hash256 topic in topics)
        {
            code.Log(0, 0, [topic]);
        }

        return code.STOP().Done;
    }

    private static Dictionary<string, object> Override(Address address, byte[] code) =>
        new() { [address.ToString()] = new { code = code.ToHexString(true) } };

    private static async Task<JsonDocument> Request(TestRpcBlockchain chain, string method, params object?[] parameters) =>
        JsonDocument.Parse(await RpcTest.TestSerializedRequest(
            chain.Container.Resolve<IRpcModuleFactory<IOverlayRpcModule>>().Create(), method, parameters));

    private static byte[] Code(JsonDocument response) =>
        Bytes.FromHexString(response.RootElement.GetProperty("result").GetProperty("code").GetString()!);

    private static int ErrorCode(JsonDocument response) =>
        response.RootElement.GetProperty("error").GetProperty("code").GetInt32();

    private static long LogIndex(JsonElement log) => long.Parse(log.GetProperty("logIndex").GetString()![2..], NumberStyles.HexNumber);
}
