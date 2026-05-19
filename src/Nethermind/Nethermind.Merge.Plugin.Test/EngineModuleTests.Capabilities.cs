// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Specs;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase(true, false, true)]
    [TestCase(false, true, false)]
    public void Engine_getBlobsV4_capability_follows_eip7843(bool eip7843Enabled, bool eip7928Enabled, bool expected)
    {
        IReleaseSpec releaseSpec = new ReleaseSpec
        {
            IsEip7843Enabled = eip7843Enabled,
            IsEip7928Enabled = eip7928Enabled,
        };
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(releaseSpec);
        EngineRpcCapabilitiesProvider engineRpcCapabilitiesProvider = new(specProvider);

        Assert.That(engineRpcCapabilitiesProvider.GetJsonRpcCapabilities()[nameof(IEngineRpcModule.engine_getBlobsV4)].IsEnabled(), Is.EqualTo(expected));
    }

    [Test]
    public void BlobCellsAndProofsV1_serializes_proofs_with_spec_name()
    {
        string json = JsonSerializer.Serialize(
            new BlobCellsAndProofsV1([new byte[] { 1 }, null], [new byte[] { 2 }, null]),
            EthereumJsonSerializer.JsonOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain("\"blob_cells\""));
            Assert.That(json, Does.Contain("\"proofs\""));
            Assert.That(json, Does.Not.Contain("\"blobCells\""));
            Assert.That(json, Does.Not.Contain("\"blob_kzg_proofs\""));
        }
    }
}
