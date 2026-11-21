// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class ReferenceStateProofTests
{
    [Test]
    public void Official_mainnet_electra_proofs_authenticate_the_reference_state([Values("current_sync_committee", "next_sync_committee", "finality_root")] string field)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "TestData", "Electra");
        BeaconStateElectra.Decode(Snappier.Snappy.DecompressToArray(File.ReadAllBytes(Path.Combine(directory, "BeaconState.ssz_snappy"))), out BeaconStateElectra state);
        Hash256 root = SszRoots.HashTreeRoot(state);
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, field + "_merkle_proof.json")));
        JsonElement proof = fixture.RootElement;
        Hash256 leaf = new(proof.GetProperty("leaf").GetString()!);
        Hash256[] branch = proof.GetProperty("branch").EnumerateArray().Select(hash => new Hash256(hash.GetString()!)).ToArray();
        int generalizedIndex = proof.GetProperty("generalizedIndex").GetInt32();

        Assert.That(LightClientStore.VerifyBranch(leaf, branch, generalizedIndex, root), Is.True);
        branch[0] = new Hash256(Enumerable.Repeat((byte)0x42, 32).ToArray());
        Assert.That(LightClientStore.VerifyBranch(leaf, branch, generalizedIndex, root), Is.False);
    }
}
