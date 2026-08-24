// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class BlockTagRewriterTests
{
    private static readonly byte[] QuotedLatest = Encoding.UTF8.GetBytes("\"latest\"");

    private static IEnumerable<TestCaseData> LocatableRequests()
    {
        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"to":"0x01"},"0x1881446",{"0x02":{"code":"0xfe"}}],"id":7,"jsonrpc":"2.0"}""",
            "\"0x1881446\"").SetName("Hex block number");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"to":"0x01"},"latest",{"0x02":{"code":"0xfe"}}],"id":7,"jsonrpc":"2.0"}""",
            "\"latest\"").SetName("Tag already latest");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"to":"0x01"},{"blockNumber":"0x10"},{}],"id":7,"jsonrpc":"2.0"}""",
            """{"blockNumber":"0x10"}""").SetName("Block specifier object");

        yield return new TestCaseData(
            """{"jsonrpc":"2.0","id":7,"method":"eth_getBalance","params":["0x01","0x1881446"]}""",
            "\"0x1881446\"").SetName("Params is the last key");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"data":"0x,\"nested\" [brace] {trap}"},"0x10",{}],"id":7}""",
            "\"0x10\"").SetName("Preceding parameter contains JSON punctuation in a string");
    }

    [TestCaseSource(nameof(LocatableRequests))]
    public void Locates_block_parameter(string request, string expected)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(request);

        Assert.That(BlockTagRewriter.TryLocateBlockParameter(utf8, out int start, out int length), Is.True);
        Assert.That(Encoding.UTF8.GetString(utf8, start, length), Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> UnreadableRequests()
    {
        yield return new TestCaseData("""{"method":"eth_blockNumber","params":[],"id":1}""")
            .SetName("Empty params");
        yield return new TestCaseData("""{"method":"eth_chainId","id":1}""")
            .SetName("No params member");
        yield return new TestCaseData("""{"method":"eth_getBalance","params":["0x01"],"id":1}""")
            .SetName("Single parameter");
        yield return new TestCaseData("""[{"method":"eth_call","params":[{},"0x1"],"id":1}]""")
            .SetName("Batch request");
        yield return new TestCaseData("""{"method":"eth_call","params":[{"to":"0x01"}""")
            .SetName("Truncated before the block parameter");
        yield return new TestCaseData("not json at all")
            .SetName("Not JSON");
    }

    [TestCaseSource(nameof(UnreadableRequests))]
    // A request the rewriter cannot understand is replayed as captured rather than dropped or throwing.
    public void Leaves_requests_it_cannot_read_alone(string request) =>
        Assert.That(BlockTagRewriter.TryLocateBlockParameter(Encoding.UTF8.GetBytes(request), out _, out _), Is.False);

    [Test]
    public void Stops_scanning_before_the_state_override_map()
    {
        // The override map is the bulk of a captured eth_call and is never needed, so the scan must end
        // at the block parameter. Malformed bytes after it prove the rewriter never looked.
        const string request = """{"method":"eth_call","params":[{"to":"0x01"},"0x1881446",{{{ not json""";

        Assert.That(BlockTagRewriter.TryLocateBlockParameter(Encoding.UTF8.GetBytes(request), out int start, out int length), Is.True);
        Assert.That(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(request), start, length), Is.EqualTo("\"0x1881446\""));
    }

    [Test]
    public void Rewrite_changes_only_the_block_parameter()
    {
        const string request = """{"method":"eth_call","params":[{"to":"0x01","data":"0xdead"},"0x1881446",{"0x02":{"balance":"0x1"}}],"id":7,"jsonrpc":"2.0"}""";
        byte[] utf8 = Encoding.UTF8.GetBytes(request);

        Assert.That(BlockTagRewriter.TryLocateBlockParameter(utf8, out int start, out int length), Is.True);

        byte[] destination = new byte[utf8.Length + QuotedLatest.Length];
        int written = BlockTagRewriter.Rewrite(utf8, start, length, QuotedLatest, destination);

        Assert.That(written, Is.EqualTo(utf8.Length - length + QuotedLatest.Length));

        JsonNode rewritten = JsonNode.Parse(Encoding.UTF8.GetString(destination, 0, written))!;
        JsonNode original = JsonNode.Parse(request)!;

        Assert.That((string?)rewritten["params"]![1], Is.EqualTo("latest"));
        Assert.That(rewritten["params"]![0]!.ToJsonString(), Is.EqualTo(original["params"]![0]!.ToJsonString()));
        Assert.That(rewritten["params"]![2]!.ToJsonString(), Is.EqualTo(original["params"]![2]!.ToJsonString()));
        Assert.That(rewritten["id"]!.ToJsonString(), Is.EqualTo(original["id"]!.ToJsonString()));
        Assert.That(rewritten["method"]!.ToJsonString(), Is.EqualTo(original["method"]!.ToJsonString()));
    }

    [Test]
    public void Rewrite_shrinks_a_longer_parameter()
    {
        // A block hash is longer than "latest", so the rewritten request is shorter than the original.
        const string request = """{"method":"eth_call","params":[{},"0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",{}],"id":1}""";
        byte[] utf8 = Encoding.UTF8.GetBytes(request);

        Assert.That(BlockTagRewriter.TryLocateBlockParameter(utf8, out int start, out int length), Is.True);

        byte[] destination = new byte[utf8.Length];
        int written = BlockTagRewriter.Rewrite(utf8, start, length, QuotedLatest, destination);

        Assert.That(written, Is.LessThan(utf8.Length));
        using JsonDocument document = JsonDocument.Parse(destination.AsMemory(0, written));
        Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("latest"));
    }

    [Test]
    public void Rewrite_reports_a_destination_that_is_too_small()
    {
        const string request = """{"method":"eth_call","params":[{},"0x1",{}],"id":1}""";
        byte[] utf8 = Encoding.UTF8.GetBytes(request);

        Assert.That(BlockTagRewriter.TryLocateBlockParameter(utf8, out int start, out int length), Is.True);
        Assert.That(BlockTagRewriter.Rewrite(utf8, start, length, QuotedLatest, new byte[4]), Is.EqualTo(-1));
    }
}
