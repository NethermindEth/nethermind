// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Block = Nethermind.Core.Block;
using PostBatch = Nethermind.Eez.Execution.Settlement.PostBatch;
using ProverV1 = Nethermind.Eez.Prove.Prover;
using ProverV2 = Nethermind.Eez.Prove.V2.Prover;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXStreamingConformanceTests
{
    [Test]
    public async Task Prove_ConsumedTerminalCheckpoint_ReturnsRecordedProof(
        [Values("streaming", "inline", "prepared")] string mode, [Values] bool complete)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, "baseline-static.json");
        ClientFrame[] requests = ConsumedRequests(document.RootElement);
        Hash256 root = ConsumedRoot(requests, complete);

        await CheckConsumedProof(document.RootElement, mode, root);
    }

    [TestCase("unknown", "streaming", TestName = "ProveStream_UnknownRegistryRoot_ReturnsNoProof")]
    [TestCase("unknown", "inline", TestName = "ProveV1_UnknownRegistryRoot_ReturnsNoProof")]
    [TestCase("unknown", "prepared", TestName = "ProveV1_PreparedUnknownRegistryRoot_ReturnsNoProof")]
    [TestCase("ordinary", "inline", TestName = "ProveV1_OrdinaryBlockIsNotTerminalPrefix_ReturnsNoProof")]
    [TestCase("state", "inline", TestName = "ProveV1_StateRootIsNotBlockCommitment_ReturnsNoProof")]
    [TestCase("load", "inline", TestName = "ProveV1_UnboundLoadCheckpoint_ReturnsNoProof")]
    public async Task Prove_UnboundRegistryRoot_ReturnsNoProof(string source, string mode)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, "baseline-static.json");
        ClientFrame[] requests = ConsumedRequests(document.RootElement);
        BlockWitness terminal = requests.Last(static frame => frame.Block is not null).Block.Data;
        Hash256 root = source switch
        {
            "ordinary" => new Hash256(requests.First(static frame => frame.Block is not null).Block.Data.Hash.Span),
            "state" => Rlp.Decode<Block>(terminal.Rlp.ToByteArray())!.Header.StateRoot!,
            "load" => LoadCheckpoint(document.RootElement, terminal),
            _ => TestItem.KeccakA,
        };
        RollupUpdate[] updates = ConsumedUpdates(requests);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.Bytes.SequenceEqual(requests.Single(static frame => frame.Begin is not null).Begin.AnchorHash.Span), Is.False,
                "precondition: the selected root differs from the authenticated parent");
            Assert.That(updates.Any(update => update.NewRoot == root.ValueHash256), Is.False,
                "precondition: this root is outside every original settlement checkpoint");
        }

        await CheckConsumedProof(document.RootElement, mode, root, "unbound");
    }

    [Test]
    public async Task Prove_ConsumedPrefix_StillChecksSettlementAndCanonicalL1(
        [Values("streaming", "inline", "prepared")] string mode, [Values("anchor", "reorg")] string mutation)
    {
        using JsonDocument document = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, "baseline-static.json");
        Hash256 root = ConsumedRoot(ConsumedRequests(document.RootElement), false);

        await CheckConsumedProof(document.RootElement, mode, root, mutation);
    }

    private static ClientFrame[] ConsumedRequests(JsonElement scenario) => scenario.GetProperty("events").EnumerateArray()
        .Where(static item => item.GetProperty("direction").GetString() == "client")
        .Select(static item => ClientFrame.Parser.ParseFrom(Convert.FromHexString(item.GetProperty("protobuf_hex").GetString()!))).ToArray();

    private static RollupUpdate[] ConsumedUpdates(ClientFrame[] requests)
    {
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(requests.Single(static frame => frame.Finalize is not null).Finalize.Settlement.AbiCalldata.Span);
        ulong rollup = requests.Single(static frame => frame.Begin is not null).Begin.RollupId;
        return batch.Entries.SelectMany(static entry => entry.RollupUpdates).Where(update => update.RollupId == rollup).ToArray();
    }

    private static Hash256 ConsumedRoot(ClientFrame[] requests, bool complete)
    {
        RollupUpdate[] updates = ConsumedUpdates(requests);
        Assert.That(updates.Length, Is.GreaterThan(1), "precondition: the original recording has an anchor and an effect");
        Block terminal = Rlp.Decode<Block>(requests.Last(static frame => frame.Block is not null).Block.Data.Rlp.ToByteArray())!;
        Hash256 root = new((complete ? updates[^1] : updates[0]).NewRoot.Bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updates[^1].NewRoot, Is.EqualTo(terminal.Hash!.ValueHash256), "precondition: the final effect binds the original full terminal block");
            Assert.That(updates[0].NewRoot, Is.Not.EqualTo(updates[^1].NewRoot), "precondition: the partial prefix leaves an unconsumed terminal effect");
            Assert.That(root.Bytes.SequenceEqual(requests.Single(static frame => frame.Begin is not null).Begin.AnchorHash.Span), Is.False,
                "precondition: consumed-prefix authorization differs from the existing parent-root path");
        }
        return root;
    }

    private static Hash256 LoadCheckpoint(JsonElement scenario, BlockWitness terminal)
    {
        string fixture = scenario.GetProperty("fixture").GetString()!;
        using JsonDocument genesis = RollupXRecordedExecutionTests.ReadSelected(DirectoryPath, fixture + ".genesis.json");
        using TempPath chain = TempPath.GetTempFile();
        File.WriteAllText(chain.Path, genesis.RootElement.GetRawText());
        (ISpecProvider specs, EezChainRules rules) = ChainConfigLoader.LoadProfile(chain.Path);
        Block decoded = Rlp.Decode<Block>(terminal.Rlp.ToByteArray())!;
        Assert.That(decoded.Transactions.Length, Is.GreaterThan(1), "precondition: load and user are separate physical transactions");
        Assert.That(decoded.Transactions[0].IsEezSystemTransaction(), Is.True, "precondition: checkpoint zero follows the setup load");
        Assert.That(decoded.Transactions[1].IsEezSystemTransaction(), Is.False, "precondition: the setup load precedes its user effect");
        using Witness witness = WindowAssembler.ToWitness(terminal.Witness);
        EezStatelessBlock block = new(terminal.Rlp.ToByteArray(), witness);
        EezStatelessBlockResult executed = new EezStatelessExecutor(specs, rules, LimboLogs.Instance).Execute([block], [-1, 0])[0];
        return executed.Checkpoints.Single(static checkpoint => checkpoint.TransactionIndex == 0).BlockHash;
    }

    private static async Task CheckConsumedProof(JsonElement scenario, string mode, Hash256 root, string? mutation = null)
    {
        StatusCode? rejection = mutation is null ? null : mutation == "reorg" ? StatusCode.Aborted : StatusCode.FailedPrecondition;
        await using RollupXStreamingServer server = await RollupXStreamingServer.StartProduction(DirectoryPath, scenario,
            reorgBeforeSigning: mutation == "reorg", registryRoot: root);
        if (mode == "streaming")
        {
            using GrpcChannel channel = server.Channel();
            Address attester = new(scenario.GetProperty("oracle").GetProperty("attester").GetString()!);
            await Replay(new ProverV2.ProverClient(channel), scenario, attester,
                mutation == "anchor" ? settlement => MutateSettlement(settlement, mutation) : null, rejection);
        }
        else
        {
            using GrpcChannel channel = server.PlainChannel();
            ProverV1.ProverClient client = new(channel);
            CapabilitiesResponse capabilities = await client.CapabilitiesAsync(new CapabilitiesRequest());
            List<ProveChunk> chunks = NativeWindow(scenario, capabilities.ValidationIdentity, mutation == "anchor" ? mutation : null);
            if (mode == "prepared")
            {
                PrepareResponse prepared = await PrepareWindow(client, chunks, capabilities.ValidationIdentity);
                chunks = [chunks[0], new ProveChunk { CachedRange = PreparedRange(prepared) }, chunks[^1]];
            }
            if (rejection is { } expectedStatus)
            {
                RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => Prove(client, chunks)))!;
                Assert.That(exception.StatusCode, Is.EqualTo(expectedStatus), "a consumed prefix never replaces complete validation and fresh L1 authorization");
            }
            else
            {
                ProveResponse response = await Prove(client, chunks);
                Proof expected = RecordedProof(scenario).Proof;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(response.PublicInputsHash, Is.EqualTo(expected.PublicInputsHash), "the original captured digest remains unchanged");
                    Assert.That(response.Signature, Is.EqualTo(expected.Proof_), "the production signer preserves the original proof oracle");
                }
            }
        }
        await server.Admission.WaitUntilIdle();
    }
}
