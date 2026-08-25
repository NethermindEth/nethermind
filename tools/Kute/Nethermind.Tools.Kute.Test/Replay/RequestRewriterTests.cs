// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class RequestRewriterTests
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

        Assert.That(RequestRewriter.TryLocateBlockParameter(utf8, out int start, out int length), Is.True);
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

    // A request the rewriter cannot understand is replayed as captured rather than dropped or throwing.
    [TestCaseSource(nameof(UnreadableRequests))]
    public void Leaves_requests_it_cannot_read_alone(string request) =>
        Assert.That(RequestRewriter.TryLocateBlockParameter(Encoding.UTF8.GetBytes(request), out _, out _), Is.False);

    [Test]
    public void Stops_scanning_before_the_state_override_map()
    {
        // The override map is the bulk of a captured eth_call and is never needed, so the scan must end
        // at the block parameter. Malformed bytes after it prove the rewriter never looked.
        const string request = """{"method":"eth_call","params":[{"to":"0x01"},"0x1881446",{{{ not json""";

        Assert.That(RequestRewriter.TryLocateBlockParameter(Encoding.UTF8.GetBytes(request), out int start, out int length), Is.True);
        Assert.That(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(request), start, length), Is.EqualTo("\"0x1881446\""));
    }

    [Test]
    public void Rewrite_changes_only_the_block_parameter()
    {
        const string request = """{"method":"eth_call","params":[{"to":"0x01","data":"0xdead"},"0x1881446",{"0x02":{"balance":"0x1"}}],"id":7,"jsonrpc":"2.0"}""";

        JsonNode rewritten = JsonNode.Parse(Rewrite(request, stripFees: false))!;
        JsonNode original = JsonNode.Parse(request)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)rewritten["params"]![1], Is.EqualTo("latest"));
            Assert.That(rewritten["params"]![0]!.ToJsonString(), Is.EqualTo(original["params"]![0]!.ToJsonString()));
            Assert.That(rewritten["params"]![2]!.ToJsonString(), Is.EqualTo(original["params"]![2]!.ToJsonString()));
            Assert.That(rewritten["id"]!.ToJsonString(), Is.EqualTo(original["id"]!.ToJsonString()));
            Assert.That(rewritten["method"]!.ToJsonString(), Is.EqualTo(original["method"]!.ToJsonString()));
        }
    }

    [Test]
    public void Rewrite_shrinks_a_longer_parameter()
    {
        // A block hash is longer than "latest", so the rewritten request is shorter than the original.
        const string request = """{"method":"eth_call","params":[{},"0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",{}],"id":1}""";

        string rewritten = Rewrite(request, stripFees: false);

        using JsonDocument document = JsonDocument.Parse(rewritten);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rewritten, Has.Length.LessThan(request.Length));
            Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("latest"));
        }
    }

    [Test]
    public void Apply_reports_a_destination_that_is_too_small()
    {
        const string request = """{"method":"eth_call","params":[{},"0x1",{}],"id":1}""";
        byte[] utf8 = Encoding.UTF8.GetBytes(request);

        RequestEdit[] edits = new RequestEdit[RequestRewriter.MaxEdits];
        int count = RequestRewriter.Plan(utf8, forceBlockParameter: true, stripFeeFields: false, edits);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(RequestRewriter.Apply(utf8, edits.AsSpan(0, count), QuotedLatest, new byte[4]), Is.EqualTo(-1));
    }

    [Test]
    public void Plan_rejects_a_buffer_too_small_for_the_worst_case()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("""{"method":"eth_call","params":[{},"0x1",{}],"id":1}""");

        Assert.That(() => RequestRewriter.Plan(utf8, true, true, new RequestEdit[RequestRewriter.MaxEdits - 1]),
            Throws.InstanceOf<ArgumentException>());
    }

    private static IEnumerable<TestCaseData> MethodPositionCases()
    {
        // The block parameter's slot is per-method. Rewriting a fixed slot would replace a storage key
        // or a trace-type list and leave the stale block behind, which reads as a working replay.
        yield return new TestCaseData(
            """{"method":"eth_getStorageAt","params":["0xabc","0x7","0x1881446"],"id":1}""",
            2).SetName("eth_getStorageAt takes the third parameter");

        yield return new TestCaseData(
            """{"method":"eth_getProof","params":["0xabc",["0x7"],"0x1881446"],"id":1}""",
            2).SetName("eth_getProof takes the third parameter");

        yield return new TestCaseData(
            """{"method":"trace_call","params":[{"to":"0xabc"},["trace"],"0x1881446"],"id":1}""",
            2).SetName("trace_call takes the third parameter");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"to":"0xabc"},"0x1881446",{}],"id":1}""",
            1).SetName("eth_call takes the second parameter");

        yield return new TestCaseData(
            """{"method":"eth_getBalance","params":["0xabc","0x1881446"],"id":1}""",
            1).SetName("eth_getBalance takes the second parameter");

        yield return new TestCaseData(
            """{"method":"eth_getBlockByNumber","params":["0x1881446",false],"id":1}""",
            0).SetName("eth_getBlockByNumber takes the first parameter");
    }

    [TestCaseSource(nameof(MethodPositionCases))]
    public void Rewrites_the_block_parameter_at_the_position_the_method_uses(string request, int index)
    {
        using JsonDocument document = JsonDocument.Parse(Rewrite(request, stripFees: false));
        JsonElement parameters = document.RootElement.GetProperty("params");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters[index].GetString(), Is.EqualTo("latest"), "the block slot is rewritten");

            for (int i = 0; i < parameters.GetArrayLength(); i++)
            {
                if (i != index)
                {
                    Assert.That(parameters[i].ToString(), Does.Not.Contain("latest"), $"parameter {i} is untouched");
                }
            }
        }
    }

    [TestCase("""{"method":"eth_getLogs","params":[{"fromBlock":"0x1"}],"id":1}""", TestName = "Unmapped method")]
    [TestCase("""{"params":[{"to":"0x01"},"0x10",{}],"id":1}""", TestName = "No method member")]
    public void Leaves_a_method_with_no_known_block_position_untouched(string request)
    {
        // Guessing a slot is worse than not rewriting: it corrupts the request and hides the stale block.
        byte[] utf8 = Encoding.UTF8.GetBytes(request);
        RequestEdit[] edits = new RequestEdit[RequestRewriter.MaxEdits];

        Assert.That(RequestRewriter.Plan(utf8, forceBlockParameter: true, stripFeeFields: false, edits), Is.Zero);
    }

    [Test]
    public void Finds_the_block_parameter_when_method_follows_params()
    {
        // Property order is not guaranteed, so the method has to be resolved either way round.
        const string request = """{"id":1,"params":[{"to":"0x01"},"0x1881446",{}],"method":"eth_call"}""";

        using JsonDocument document = JsonDocument.Parse(Rewrite(request, stripFees: false));

        Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("latest"));
    }

    private static IEnumerable<TestCaseData> FeeStrippingCases()
    {
        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"from":"0x01","gasPrice":"0x71afd498d0","gas":"0x77359400","data":"0xab"},"0x10",{}],"id":1}""",
            new[] { "from", "gas", "data" }).SetName("Fee field in the middle");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"gasPrice":"0x1","gas":"0x2"},"0x10",{}],"id":1}""",
            new[] { "gas" }).SetName("Fee field is first");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"from":"0x01","gasPrice":"0x1"},"0x10",{}],"id":1}""",
            new[] { "from" }).SetName("Fee field is last");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"gasPrice":"0x1"},"0x10",{}],"id":1}""",
            Array.Empty<string>()).SetName("Fee field is the only property");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"from":"0x01","gasPrice":"0x1","maxFeePerGas":"0x2"},"0x10",{}],"id":1}""",
            new[] { "from" }).SetName("Adjacent fee fields, trailing");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"gasPrice":"0x1","maxFeePerGas":"0x2"},"0x10",{}],"id":1}""",
            Array.Empty<string>()).SetName("Adjacent fee fields, only properties");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"gasPrice":"0x1","maxFeePerGas":"0x2","gas":"0x3"},"0x10",{}],"id":1}""",
            new[] { "gas" }).SetName("Adjacent fee fields at the front");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"maxPriorityFeePerGas":"0x1","from":"0x02","maxFeePerGas":"0x3"},"0x10",{}],"id":1}""",
            new[] { "from" }).SetName("Fee fields either side of a kept property");

        yield return new TestCaseData(
            """{"method":"eth_call","params":[{"from":"0x01","gas":"0x2"},"0x10",{}],"id":1}""",
            new[] { "from", "gas" }).SetName("No fee field to strip");
    }

    [TestCaseSource(nameof(FeeStrippingCases))]
    public void Strips_fee_fields_and_leaves_valid_json(string request, string[] expectedKeys)
    {
        // A capture's fee fields were priced against the base fee at capture time, so replaying them at
        // a later head rejects calls before they execute. Removing them has to leave the object
        // parseable, which means taking exactly one comma with each removal.
        string rewritten = Rewrite(request, stripFees: true);

        using JsonDocument document = JsonDocument.Parse(rewritten);
        JsonElement call = document.RootElement.GetProperty("params")[0];

        List<string> actual = [];
        foreach (JsonProperty property in call.EnumerateObject())
        {
            actual.Add(property.Name);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expectedKeys));
            Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("latest"));
            Assert.That(RequestRewriter.HasFeeField(Encoding.UTF8.GetBytes(rewritten)), Is.False);
        }
    }

    [Test]
    public void Leaves_fee_fields_alone_when_not_asked_to_strip()
    {
        const string request = """{"method":"eth_call","params":[{"from":"0x01","gasPrice":"0x1"},"0x10",{}],"id":1}""";

        using JsonDocument document = JsonDocument.Parse(Rewrite(request, stripFees: false));

        Assert.That(document.RootElement.GetProperty("params")[0].TryGetProperty("gasPrice", out _), Is.True);
    }

    [Test]
    public void Does_not_strip_a_fee_field_from_the_override_map()
    {
        // Only the call object loses its fee fields. An override entry holding a similarly named key must
        // survive, and the scan should never reach it.
        const string request = """{"method":"eth_call","params":[{"from":"0x01","gasPrice":"0x1"},"0x10",{"0x02":{"gasPrice":"0x9","balance":"0x1"}}],"id":1}""";

        using JsonDocument document = JsonDocument.Parse(Rewrite(request, stripFees: true));
        JsonElement overrides = document.RootElement.GetProperty("params")[2];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overrides.GetProperty("0x02").TryGetProperty("gasPrice", out _), Is.True);
            Assert.That(document.RootElement.GetProperty("params")[0].TryGetProperty("gasPrice", out _), Is.False);
        }
    }

    [TestCase("""{"method":"eth_call","params":[{"from":"0x01"},"latest",{}],"id":1}""", false, TestName = "No fee field")]
    [TestCase("""{"method":"eth_call","params":[{"gasPrice":"0x1"},"latest",{}],"id":1}""", true, TestName = "Has gasPrice")]
    [TestCase("""{"method":"eth_call","params":[{"maxFeePerGas":"0x1"},"latest",{}],"id":1}""", true, TestName = "Has maxFeePerGas")]
    public void Reports_whether_a_request_still_carries_a_fee_field(string request, bool expected) =>
        Assert.That(RequestRewriter.HasFeeField(Encoding.UTF8.GetBytes(request)), Is.EqualTo(expected));

    /// <summary>Plans and applies the replay edits, returning the rewritten request as text.</summary>
    private static string Rewrite(string request, bool stripFees)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(request);
        RequestEdit[] edits = new RequestEdit[RequestRewriter.MaxEdits];
        int count = RequestRewriter.Plan(utf8, forceBlockParameter: true, stripFeeFields: stripFees, edits);

        ReadOnlySpan<RequestEdit> planned = edits.AsSpan(0, count);
        byte[] destination = new byte[RequestRewriter.RewrittenLength(utf8, planned, QuotedLatest)];
        int written = RequestRewriter.Apply(utf8, planned, QuotedLatest, destination);

        Assert.That(written, Is.EqualTo(destination.Length));

        return Encoding.UTF8.GetString(destination, 0, written);
    }
}
