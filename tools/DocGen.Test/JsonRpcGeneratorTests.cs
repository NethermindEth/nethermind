// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.DocGen.Test;

[TestFixture]
public class JsonRpcGeneratorTests
{
    private string _docsRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _docsRoot = Path.Combine(Path.GetTempPath(), $"nethermind-docgen-{Guid.NewGuid():N}");
        string rpcDirectory = Path.Combine(_docsRoot, "docs", "interacting", "json-rpc-ns");
        Directory.CreateDirectory(rpcDirectory);
        File.WriteAllText(Path.Combine(rpcDirectory, "eth_subscribe.md"), string.Empty);
        File.WriteAllText(Path.Combine(rpcDirectory, "eth_unsubscribe.md"), string.Empty);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_docsRoot, recursive: true);

    [Test]
    public void Trace_callMany_documents_calls_as_wire_array_of_pairs()
    {
        JsonRpcGenerator.Generate(_docsRoot);

        string traceDoc = File.ReadAllText(Path.Combine(_docsRoot, "docs", "interacting", "json-rpc-ns", "trace.md"));
        int start = traceDoc.IndexOf("### trace_callMany\n", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        int end = traceDoc.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        string methodDoc = end < 0 ? traceDoc[start..] : traceDoc[start..end];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(methodDoc, Does.Contain("`calls`: array of [transaction object, array of trace type strings] pairs"));
            Assert.That(methodDoc, Does.Not.Contain("- `calls`:"));
            Assert.That(methodDoc, Does.Contain("`blockParameter`: _string_"));
        }
    }
}
