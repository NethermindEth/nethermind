// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Adapter;

public class NethermindNodeAdapterTests
{
    [Test]
    public void GetNodeVersion_returns_client_version_and_runtime()
    {
        INethermindApi api = Substitute.For<INethermindApi>();

        INethermindNodeAdapter adapter = new NethermindNodeAdapter(api);

        NodeVersionDto version = adapter.GetNodeVersion();

        Assert.That(version.ClientVersion, Is.EqualTo(ProductInfo.ClientId));
        Assert.That(version.DotNetRuntime, Does.StartWith(".NET"));
        Assert.That(version.OperatingSystem, Is.Not.Empty);
    }
}
