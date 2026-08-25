// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.CommandLine.Parsing;
using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test.Replay;

public class ReplayCommandTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() =>
        _directory = Directory.CreateTempSubdirectory(nameof(ReplayCommandTests)).FullName;

    [TearDown]
    public void TearDown() =>
        Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Fails_a_dry_run_over_an_empty_trace()
    {
        // A level that sent nothing has a zero failure rate, so without its own gate an empty trace
        // would exit as a passing benchmark.
        string path = WriteTrace(string.Empty);
        string[] args = ["-i", path, "--dry-run"];

        int exitCode = await ReplayCommand.Create().Parse(args).InvokeAsync();

        Assert.That(exitCode, Is.EqualTo(2));
    }

    [Test]
    public async Task Fails_when_skip_reaches_past_the_trace()
    {
        string path = WriteTrace("""{"method":"eth_call","params":[{"to":"0x01"},"latest",{}],"id":1,"jsonrpc":"2.0"}""" + '\n');
        await using StubJsonRpcServer server = new();
        string[] args = ["-i", path, "-a", server.Address.ToString(), "-c", "1", "-n", "0", "-w", "0", "--skip", "5"];

        int exitCode = await ReplayCommand.Create().Parse(args).InvokeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(server.Requests, Is.Zero);
        }
    }

    [TestCase("--requests=-1")]
    [TestCase("--warmup=-1")]
    [TestCase("--skip=-1")]
    [TestCase("--duration=-1")]
    [TestCase("--timeout=0")]
    [TestCase("--max-failure-rate=NaN")]
    [TestCase("--block=lastest")]
    [TestCase("--block=0x")]
    [TestCase("--block=late\"st")]
    public void Rejects_an_out_of_range_option(string option)
    {
        // -1 requests would silently mean the whole trace, a zero timeout throws from inside
        // HttpClient, and a NaN failure rate compares false against everything, disabling the gate.
        string[] args = ["-i", "trace.jsonl", option];

        ParseResult result = ReplayCommand.Create().Parse(args);

        Assert.That(result.Errors, Has.Some.Matches<ParseError>(static error => error.Message.Contains("must be")));
    }

    [Test]
    public void Rejects_an_empty_block_tag()
    {
        // An empty tag is not 'keep', so every request would be rewritten to "" and fail at the node.
        string[] args = ["-i", "trace.jsonl", "-b", ""];

        ParseResult result = ReplayCommand.Create().Parse(args);

        Assert.That(result.Errors, Has.Some.Matches<ParseError>(static error => error.Message.Contains("must be")));
    }

    [Test]
    public void Accepts_the_documented_zero_values()
    {
        string[] args = ["-i", "trace.jsonl", "-n", "0", "-w", "0", "--skip", "0", "-d", "0", "-b", "finalized"];

        Assert.That(ReplayCommand.Create().Parse(args).Errors, Is.Empty);
    }

    private string WriteTrace(string content)
    {
        string path = Path.Combine(_directory, "trace.jsonl");
        File.WriteAllText(path, content);

        return path;
    }
}
