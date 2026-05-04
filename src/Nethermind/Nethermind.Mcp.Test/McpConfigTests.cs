// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Mcp.Test;

public class McpConfigTests
{
    [Test]
    public void Defaults_match_design()
    {
        IMcpConfig config = new McpConfig();

        Assert.That(config.Enabled, Is.False);
        Assert.That(config.HttpEnabled, Is.True);
        Assert.That(config.HttpHost, Is.EqualTo("127.0.0.1"));
        Assert.That(config.HttpPort, Is.EqualTo(8550));
        Assert.That(config.ApiKey, Is.Null);
        Assert.That(config.MaxConcurrent, Is.EqualTo(4));
    }
}
