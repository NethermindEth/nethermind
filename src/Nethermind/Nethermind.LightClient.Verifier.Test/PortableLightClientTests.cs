// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.LightClient.Test;

namespace Nethermind.LightClient.Verifier.Test;

[TestFixture]
public class PortableLightClientTests
{
    [Test]
    public async Task Applies_only_currently_verifiable_updates(
        [Values("update", "finality", "optimistic")] string kind,
        [Values("premature", "valid", "signature", "proof")] string scenario)
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        ulong currentSlot = spec.GetSlotAtTime((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(currentSlot - 5, 1);
        LightClientUpdate update = ConsensusTests.Update(currentSlot - 4, currentSlot - 3,
            scenario == "premature" ? currentSlot + 8192 : currentSlot - 2, 1, nextKey: 1);
        ConsensusTests.Sign(update, 1, 512, spec);
        if (scenario == "signature")
        {
            byte[] signature = update.SyncAggregate!.SyncCommitteeSignature.Bytes.ToArray();
            signature[32] ^= 1;
            update.SyncAggregate.SyncCommitteeSignature = new BlsSignature(signature);
        }
        if (scenario == "proof") update.AttestedHeader!.ExecutionBranch![0] = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindPortableLightClientTests", Guid.NewGuid().ToString("N"));
        try
        {
            using PortableLightClient client = new("mainnet", SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!).ToString(), directory);
            await client.InitializeAsync(LightClientWireCodec.EncodeBootstrap(bootstrap, spec), null, null, null, null);
            string finalized = client.GetHead("finalized");
            string optimistic = client.GetHead("latest");
            (byte[] Chain, byte[]? Head, byte[]? Optimistic, byte[]? Best) before = await client.ExportJournalAsync();
            byte[] record = kind switch
            {
                "update" => LightClientWireCodec.EncodeUpdate(update, spec),
                "finality" => LightClientWireCodec.EncodeFinality(new()
                {
                    AttestedHeader = update.AttestedHeader,
                    FinalizedHeader = update.FinalizedHeader,
                    FinalityBranch = update.FinalityBranch,
                    SyncAggregate = update.SyncAggregate,
                    SignatureSlot = update.SignatureSlot,
                }, spec),
                _ => LightClientWireCodec.EncodeOptimistic(new()
                {
                    AttestedHeader = update.AttestedHeader,
                    SyncAggregate = update.SyncAggregate,
                    SignatureSlot = update.SignatureSlot,
                }, spec),
            };
            if (scenario is "signature" or "proof")
                Assert.That(async () => await client.ApplyAsync(kind, record), Throws.TypeOf<InvalidDataException>());
            else Assert.That(await client.ApplyAsync(kind, record), Is.EqualTo(scenario == "valid"));
            if (scenario == "valid")
            {
                Assert.That(client.GetHead("latest"), Is.Not.EqualTo(optimistic));
                return;
            }
            (byte[] Chain, byte[]? Head, byte[]? Optimistic, byte[]? Best) after = await client.ExportJournalAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(client.GetHead("finalized"), Is.EqualTo(finalized));
                Assert.That(client.GetHead("latest"), Is.EqualTo(optimistic));
                Assert.That(after.Chain, Is.EqualTo(before.Chain));
                Assert.That(after.Head, Is.EqualTo(before.Head));
                Assert.That(after.Optimistic, Is.EqualTo(before.Optimistic));
                Assert.That(after.Best, Is.EqualTo(before.Best));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
