// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

[Parallelizable(ParallelScope.All)]
public class GethTraceOptionsDeserializationTests
{
    // The node reads RPC parameters through the source-generated EthRpcJsonContext, which assigns every init-only member;
    // the legacy disableMemory alias must decide memory capture as reflection did: the last of the two in the JSON wins.
    [TestCase("{}", false)]
    [TestCase("""{"disableMemory":false}""", true)]
    [TestCase("""{"disableMemory":true}""", false)]
    [TestCase("""{"enableMemory":true}""", true)]
    [TestCase("""{"enableMemory":false}""", false)]
    [TestCase("""{"disableMemory":true,"enableMemory":true}""", true)]
    [TestCase("""{"enableMemory":true,"disableMemory":true}""", false)]
    public void Memory_capture_follows_the_options_present(string json, bool enableMemory)
    {
        GethTraceOptions options = JsonSerializer.Deserialize<GethTraceOptions>(json, EthereumJsonSerializer.JsonRpcRequestOptions)!;

        Assert.That(options.EnableMemory, Is.EqualTo(enableMemory));
    }
}