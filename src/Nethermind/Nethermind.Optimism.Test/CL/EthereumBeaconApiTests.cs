// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class EthereumBeaconApiTests
{
    [Test]
    public void Blob_sidecars_response_reads_blobs_and_commitments()
    {
        const string json = """{"data":[{"index":"0","blob":"0x0102","kzg_commitment":"0x0304","kzg_proof":"0x05"}]}""";

        // Derivation retries a response it cannot read forever, so a beacon response must always decode.
        EthereumBeaconApi.GetBlobSidecarsResponse response = new EthereumJsonSerializer().Deserialize<EthereumBeaconApi.GetBlobSidecarsResponse>(json)!;

        Assert.That(response.Data, Has.Length.EqualTo(1));
        Assert.That(response.Data[0].Blob, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(response.Data[0].KzgCommitment, Is.EqualTo(new byte[] { 3, 4 }));
    }
}
