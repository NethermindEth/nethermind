// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

[NonParallelizable]
public sealed class NativeStreamingObservationTests
{
    private const string Capture = "rollupx/native-history-observations-v6";

    [TestCase("static-before-history.v2.protobuf")]
    [TestCase("static-before-ordinary.v2.protobuf")]
    [TestCase("static-correct.v2.protobuf")]
    [TestCase("lookup-before-history.v2.protobuf")]
    [TestCase("lookup-before-ordinary.v2.protobuf")]
    [TestCase("lookup-correct.v2.protobuf")]
    public async Task Finalize_UpstreamObservation_UsesItsExactTransactionPosition(string filename)
    {
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(
            File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "l2-genesis.json")), NativeSystemTxFormat.ContractV6);
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "l1-genesis.json")));
        NativeStreamingProfile profile = new(genesis, l1);
        using JsonDocument identity = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(Capture, "identity.json")));
        JsonElement metadata = identity.RootElement;
        JsonElement selected = default;
        foreach (JsonElement item in metadata.GetProperty("cases").EnumerateArray())
            if (item.GetProperty("request").GetString() == filename) selected = item;
        Assert.That(selected.ValueKind, Is.EqualTo(JsonValueKind.Object));
        byte[] request = File.ReadAllBytes(StatelessFixtures.PathOf(Capture, filename));
        Assert.That(ValueKeccak.Compute(request), Is.EqualTo(new ValueHash256(selected.GetProperty("request_hash").GetString()!)));
        EezSettlementContext context = new(metadata.GetProperty("rollup_id").GetUInt64(), genesis.Specs.ChainId,
            new Address(metadata.GetProperty("proof_system").GetString()!), new ValueHash256(metadata.GetProperty("vkey").GetString()!),
            genesis.Rules.Schedule!.BlockIntervalSeconds);
        using PrivateKey key = new("8b3a350cf5c34c9194ca85829a2df0ec3153be0318b5e2d3348e872092edffba");
        EezAttestationSigner signer = new(key);
        NativeOfflineFinalizer finalizer = new(profile, context,
            new Address(metadata.GetProperty("registry").GetString()!), new Address(metadata.GetProperty("manager").GetString()!),
            signer, LimboLogs.Instance);
        StreamingBlockExecutor executor = new(profile, LimboLogs.Instance);
        StreamingRegistryLimits limits = new(2, 2, 128 * 1024 * 1024, TimeSpan.FromMinutes(10));
        using StreamingSessionRegistry sessions = new(limits, new StreamingSessionLimits(64, 8 * 1024 * 1024, 100_000),
            TimeProvider.System, StreamingSettlementProfile.RollupXNative, profile);
        StreamingConnection connection = new(sessions, "composer", context.RollupId);
        try
        {
            await Program.InitializeExecution(genesis.Rules, TxDecoder.Instance);
            using MemoryStream input = new(request, writable: false);
            ServerFrame previous = connection.Dispatch(ClientFrame.Parser.ParseDelimitedFrom(input)).Response!;
            int finalized = 0;
            while (input.Position < input.Length)
            {
                ClientFrame frame = ClientFrame.Parser.ParseDelimitedFrom(input);
                frame.SessionId = previous.SessionId;
                frame.Epoch = previous.Epoch;
                SessionOperation operation = connection.Dispatch(frame).Operation!;
                Assert.That(operation, Is.Not.Null);
                try
                {
                    if (frame.Block is not null)
                        previous = connection.CommitBlock(operation, executor.Execute(operation, CancellationToken.None));
                    else
                    {
                        Assert.That(frame.Finalize, Is.Not.Null);
                        finalized++;
                        if (!selected.GetProperty("accept").GetBoolean())
                        {
                            RpcException? rejected = await Assert.ThrowsAsync<RpcException>(() => finalizer.Finalize(operation, CancellationToken.None));
                            Assert.That(rejected!.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
                        }
                        else
                        {
                            Proof proof = await finalizer.Finalize(operation, CancellationToken.None);
                            previous = connection.CompleteProof(operation, proof);
                            ValueHash256 digest = new(selected.GetProperty("public_inputs_hash").GetString()!);
                            using (Assert.EnterMultipleScope())
                            {
                                Assert.That(new ValueHash256(proof.PublicInputsHash.Span), Is.EqualTo(digest));
                                Assert.That(proof.Proof_.Length, Is.EqualTo(65));
                            }
                            Signature signature = new(proof.Proof_.Span[..64], proof.Proof_.Span[64] - 27);
                            Assert.That(new EthereumEcdsa(0).RecoverAddress(signature, in digest),
                                Is.EqualTo(new Address(metadata.GetProperty("attester").GetString()!)));
                        }
                    }
                }
                finally { connection.FinishOperation(operation); }
            }
            Assert.That(finalized, Is.EqualTo(1));
        }
        finally
        {
            TxDecoder.Instance.RegisterDecoder(new EezNativeSystemTxDecoder());
            TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder());
        }
    }
}
