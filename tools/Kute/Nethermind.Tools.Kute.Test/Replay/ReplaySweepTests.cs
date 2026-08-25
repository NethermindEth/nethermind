// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;
using Nethermind.Tools.Kute.Replay;
using NUnit.Framework;
using ZstdSharp;

namespace Nethermind.Tools.Kute.Test.Replay;

public class ReplaySweepTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() =>
        _directory = Directory.CreateTempSubdirectory(nameof(ReplaySweepTests)).FullName;

    [TearDown]
    public void TearDown() =>
        Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Forces_the_block_tag_on_every_request()
    {
        // The whole point of the harness: a trace captured against historical blocks must land on the
        // node's current head, or every call is answered from a state the node no longer has.
        string path = WriteTrace(".jsonl", Requests(20, index => index % 2 == 0 ? "latest" : $"0x{index:x}"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            MeasuredRequests = 20,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Requests, Is.EqualTo(20));
            Assert.That(results[0].Rewritten, Is.EqualTo(10), "only the records that were not already 'latest' need rewriting");
        }

        foreach (string body in server.Bodies)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("latest"));
        }
    }

    [Test]
    public async Task Keeps_the_captured_tag_when_asked_to()
    {
        string path = WriteTrace(".jsonl", Requests(6, index => $"0x{index:x}"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            BlockTag = null,
            // One worker, so the order requests arrive in is the order the trace holds them.
            Concurrencies = [1],
            MeasuredRequests = 6,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Rewritten, Is.Zero);
            Assert.That(server.Bodies, Has.Count.EqualTo(6));
        }

        for (int i = 0; i < server.Bodies.Count; i++)
        {
            using JsonDocument document = JsonDocument.Parse(server.Bodies[i]);
            Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo($"0x{i:x}"));
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(8)]
    public async Task Keeps_exactly_the_requested_number_of_requests_in_flight(int concurrency)
    {
        // A sweep only means something if the level label is the load actually offered. The server
        // answers nothing until exactly this many requests are open at once, so a harness holding
        // fewer can never finish and one holding more is caught by the peak.
        string path = WriteTrace(".jsonl", Requests(concurrency * 6, _ => "latest"));
        await using StubJsonRpcServer server = new(releaseAt: concurrency);

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [concurrency],
            MeasuredRequests = concurrency * 6,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Concurrency, Is.EqualTo(concurrency));
            Assert.That(results[0].Total, Is.EqualTo(concurrency * 6), "the barrier released, so every request completed");
            Assert.That(server.PeakInFlight, Is.EqualTo(concurrency), "never more in flight than the level");
            Assert.That(server.Connections, Is.LessThanOrEqualTo(concurrency));
        }
    }

    [Test]
    public async Task Excludes_warm_up_requests_from_the_measurement()
    {
        // Warm-up traffic must reach the node, so caches and JIT are warm, but must not enter the
        // latency distribution, where it would show up as a fat tail that is really just cold start.
        string path = WriteTrace(".jsonl", Requests(50, _ => "latest"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            MeasuredRequests = 10,
            WarmupRequests = 5,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Requests, Is.EqualTo(2 + 5 + 10), "the node sees priming, warm-up and measured traffic");
            Assert.That(results[0].Total, Is.EqualTo(10), "only the measured window is reported");
            Assert.That(results[0].Latencies, Has.Count.EqualTo(10));
        }
    }

    [Test]
    public async Task Measures_the_request_window_not_the_whole_pass()
    {
        // Elapsed drives the reported throughput, so it has to span the measured requests alone. The
        // unmeasured prefix is sized to dwarf the measured window, so timing the whole pass instead
        // cannot stay under the bound below, while a scheduler stall would have to reach seconds to
        // push the correct window over it.
        const int warmup = 40;
        TimeSpan delay = TimeSpan.FromMilliseconds(25);

        string path = WriteTrace(".jsonl", Requests(50, _ => "latest"));
        await using StubJsonRpcServer server = new(delay: delay);

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [1],
            MeasuredRequests = 6,
            WarmupRequests = warmup,
        });

        LevelResult result = results[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Elapsed, Is.GreaterThanOrEqualTo(result.Max), "the window contains every request");
            Assert.That(result.Elapsed, Is.LessThan(delay * warmup), "the unmeasured prefix stays outside the window");
        }
    }

    [Test]
    public async Task Primes_every_connection_before_measuring()
    {
        // The pool opens a connection per concurrent request, so the level's connections only all open
        // up front if the priming burst is genuinely simultaneous. Warm-up and measured passes are one
        // request each, so the burst is the only phase that can put this many in flight at once - and
        // the only way the run ends up with this many connections.
        const int concurrency = 4;
        string path = WriteTrace(".jsonl", Requests(40, _ => "latest"));
        await using StubJsonRpcServer server = new(releaseAt: concurrency);

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [concurrency],
            MeasuredRequests = 1,
            WarmupRequests = 1,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Total, Is.EqualTo(1), "the measured window is untouched");
            Assert.That(server.Requests, Is.EqualTo(concurrency + 1 + 1), "the prime is one request per connection");
            Assert.That(server.Connections, Is.EqualTo(concurrency), "the burst alone opened every connection");
        }
    }

    [Test]
    public async Task Measures_records_the_warm_up_did_not_touch()
    {
        // A warm-up that replays the same records the measured window then replays would serve that
        // window from caches those exact requests just warmed, flattering p50.
        string path = WriteTrace(".jsonl", Requests(30, index => $"0x{index:x}"));
        await using StubJsonRpcServer server = new();

        await Run(server, path, options => options with
        {
            BlockTag = null,
            Concurrencies = [1],
            MeasuredRequests = 5,
            WarmupRequests = 3,
            Skip = 2,
        });

        // One priming request and a three-record warm-up, all drawn from records 2-4, then the
        // measured window starting at record 5.
        IReadOnlyList<string> bodies = server.Bodies;
        Assert.That(bodies, Has.Count.EqualTo(1 + 3 + 5));

        for (int i = 0; i < 5; i++)
        {
            using JsonDocument document = JsonDocument.Parse(bodies[4 + i]);
            Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo($"0x{5 + i:x}"));
        }
    }

    [Test]
    public async Task Warns_when_requests_cannot_carry_the_forced_tag()
    {
        // eth_getLogs keeps its range inside a filter object the rewriter cannot reach, so those
        // requests replay at their captured range; the run must say so rather than look retagged.
        string[] records = new string[4];
        for (int i = 0; i < records.Length; i++)
        {
            records[i] = $"{{\"method\":\"eth_getLogs\",\"params\":[{{\"fromBlock\":\"0x{i:x}\"}}],\"id\":{i},\"jsonrpc\":\"2.0\"}}";
        }

        string path = WriteTrace(".jsonl", records);
        await using StubJsonRpcServer server = new();
        StringWriter log = new();

        ReplayOptions options = new()
        {
            InputPath = path,
            Address = server.Address,
            Concurrencies = [2],
            MeasuredRequests = 4,
            WarmupRequests = 0,
            StripFeeFields = false,
            Timeout = TimeSpan.FromSeconds(30),
        };

        IReadOnlyList<LevelResult> results = await new ReplaySweep(options, log).RunAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Untagged, Is.EqualTo(4));
            Assert.That(log.ToString(), Does.Contain("without the forced block tag"));
        }
    }

    [Test]
    public async Task Treats_an_omitted_block_parameter_as_latest()
    {
        // eth_call's block parameter is optional and defaults to latest, so a record without it is
        // already at the forced tag and must not be counted or warned as untagged.
        const string record = """{"method":"eth_call","params":[{"to":"0x01"}],"id":1,"jsonrpc":"2.0"}""";
        string path = WriteTrace(".jsonl", [record]);
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [1],
            MeasuredRequests = 0,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Untagged, Is.Zero);
            Assert.That(server.Bodies[0], Is.EqualTo(record), "nothing to edit, so the record is byte-identical");
        }
    }

    [Test]
    public async Task Stops_a_level_once_its_duration_cap_expires()
    {
        // The cap has to gate sending, not just enqueueing. The channel holds twice the concurrency, so
        // gating only the reader lets that whole backlog through after the level should have ended. The
        // response delay is sized so only two rounds fit inside the cap: a harness that also drains its
        // backlog sends roughly double that, which the bound below rejects.
        const int concurrency = 16;
        TimeSpan delay = TimeSpan.FromMilliseconds(150);
        TimeSpan cap = TimeSpan.FromMilliseconds(200);

        string path = WriteTrace(".jsonl", Requests(concurrency * 20, index => $"0x{index:x}"));
        await using StubJsonRpcServer server = new(delay: delay);

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [concurrency],
            MeasuredRequests = concurrency * 20,
            WarmupRequests = 0,
            MaxDuration = cap,
        });

        LevelResult result = results[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Total, Is.GreaterThan(0), "the level ran");
            Assert.That(result.Total, Is.LessThan(concurrency * 20), "the cap stopped it early");
            Assert.That(server.Requests, Is.EqualTo(result.Total), "nothing was sent that was not measured");
            Assert.That(
                server.Requests,
                Is.LessThanOrEqualTo(concurrency * 3),
                "expiry dropped the queued backlog instead of sending it");
            Assert.That(result.Rewritten, Is.EqualTo(result.Total),
                "edits count when sent, so the dropped backlog reports none");
        }
    }

    [Test]
    public async Task Ignores_the_duration_cap_when_none_is_set()
    {
        string path = WriteTrace(".jsonl", Requests(20, _ => "latest"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [4],
            MeasuredRequests = 0,
            WarmupRequests = 0,
            MaxDuration = null,
        });

        Assert.That(results[0].Total, Is.EqualTo(20));
    }

    [Test]
    public async Task Replays_the_same_prefix_at_every_level()
    {
        // Levels are only comparable if they answer the same requests, so each level restarts the trace.
        string path = WriteTrace(".jsonl", Requests(30, _ => "latest"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [1, 2, 4],
            MeasuredRequests = 5,
            WarmupRequests = 0,
        });

        int[] concurrencies = new int[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            concurrencies[i] = results[i].Concurrency;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(concurrencies, Is.EqualTo(new[] { 1, 2, 4 }));
            Assert.That(server.Requests, Is.EqualTo(15));
        }

        // Concurrent levels may answer in any order, so compare the sets each level was given.
        string[] firstLevel = SortedSlice(server.Bodies, 0, 5);
        string[] secondLevel = SortedSlice(server.Bodies, 5, 5);
        string[] thirdLevel = SortedSlice(server.Bodies, 10, 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondLevel, Is.EqualTo(firstLevel));
            Assert.That(thirdLevel, Is.EqualTo(firstLevel));
        }
    }

    [Test]
    public async Task Skips_leading_records()
    {
        string path = WriteTrace(".jsonl", Requests(10, index => $"0x{index:x}"));
        await using StubJsonRpcServer server = new();

        await Run(server, path, options => options with
        {
            BlockTag = null,
            Concurrencies = [1],
            Skip = 7,
            MeasuredRequests = 0,
            WarmupRequests = 0,
        });

        using JsonDocument document = JsonDocument.Parse(server.Bodies[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Bodies, Has.Count.EqualTo(3));
            Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("0x7"));
        }
    }

    [Test]
    public async Task Replays_the_whole_trace_when_no_request_count_is_given()
    {
        string path = WriteTrace(".jsonl.zst", Requests(37, _ => "latest"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            MeasuredRequests = 0,
            WarmupRequests = 0,
        });

        Assert.That(results[0].Total, Is.EqualTo(37));
    }

    [Test]
    public async Task Strips_fee_fields_from_every_request()
    {
        // A stale gasPrice makes the node reject the call before it executes, and the rejected share
        // drifts with the base fee, so the run silently gets faster as the network moves.
        string path = WriteTrace(".jsonl", FeeBearingRequests(12));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            StripFeeFields = true,
            MeasuredRequests = 12,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].FeesStripped, Is.EqualTo(12));
            Assert.That(server.Bodies, Has.Count.EqualTo(12));
        }

        foreach (string body in server.Bodies)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement call = document.RootElement.GetProperty("params")[0];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(call.TryGetProperty("gasPrice", out _), Is.False);
                Assert.That(call.GetProperty("gas").GetString(), Is.EqualTo("0x77359400"), "only fee fields go");
                Assert.That(document.RootElement.GetProperty("params")[1].GetString(), Is.EqualTo("latest"));
            }
        }
    }

    [Test]
    public async Task Keeps_fee_fields_when_asked_to()
    {
        string path = WriteTrace(".jsonl", FeeBearingRequests(5));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            StripFeeFields = false,
            MeasuredRequests = 5,
            WarmupRequests = 0,
        });

        Assert.That(results[0].FeesStripped, Is.Zero);

        foreach (string body in server.Bodies)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.That(document.RootElement.GetProperty("params")[0].TryGetProperty("gasPrice", out _), Is.True);
        }
    }

    private static IEnumerable<TestCaseData> FailureCases()
    {
        yield return new TestCaseData(
            (Func<string, (HttpStatusCode, string)>)(_ => (HttpStatusCode.OK, """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"out of gas"}}""")),
            nameof(LevelResult.RpcErrors)).SetName("JSON-RPC error member");

        yield return new TestCaseData(
            (Func<string, (HttpStatusCode, string)>)(_ => (HttpStatusCode.InternalServerError, "boom")),
            nameof(LevelResult.HttpErrors)).SetName("Non-success status");
    }

    [TestCaseSource(nameof(FailureCases))]
    public async Task Separates_failure_kinds(Func<string, (HttpStatusCode Status, string Body)> responder, string expectedCounter)
    {
        // A degrading node fails differently from a saturated socket layer, and a sweep that lumps them
        // together hides which one happened.
        string path = WriteTrace(".jsonl", Requests(8, _ => "latest"));
        await using StubJsonRpcServer server = new(responder);

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            MeasuredRequests = 8,
            WarmupRequests = 0,
        });

        LevelResult result = results[0];
        int counted = expectedCounter switch
        {
            nameof(LevelResult.RpcErrors) => result.RpcErrors,
            nameof(LevelResult.HttpErrors) => result.HttpErrors,
            _ => result.TransportErrors,
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.Zero);
            Assert.That(result.Failed, Is.EqualTo(8));
            Assert.That(result.FailureRate, Is.EqualTo(1d));
            Assert.That(counted, Is.EqualTo(8));
        }
    }

    [Test]
    public async Task Counts_a_large_result_as_a_success()
    {
        // Only the head of a response is buffered for error detection, so a result larger than that
        // buffer must not be mistaken for a failure.
        string big = new('a', 200_000);
        string path = WriteTrace(".jsonl", Requests(4, _ => "latest"));
        await using StubJsonRpcServer server = new(_ => (HttpStatusCode.OK, $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x{big}\"}}"));

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            MeasuredRequests = 4,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Succeeded, Is.EqualTo(4));
            Assert.That(results[0].Failed, Is.Zero);
        }
    }

    [TestCase("""[{"jsonrpc":"2.0","id":1,"result":"0x1"},{"jsonrpc":"2.0","id":2,"result":"0x2"}]""", true, TestName = "Batch of results")]
    [TestCase("""[{"jsonrpc":"2.0","id":1,"result":"0x1"},{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"boom"}}]""", false, TestName = "Batch containing an error")]
    [TestCase("[]", false, TestName = "Empty batch")]
    [TestCase("[42]", false, TestName = "Batch entry that is not a response")]
    public async Task Classifies_batch_responses_by_their_entries(string responseBody, bool success)
    {
        // A batch has no single decisive member: an entry-blind classifier would call a batch of pure
        // errors a success, and error rates are what the sweep's failure gate keys on.
        string path = WriteTrace(".jsonl", Requests(3, _ => "latest"));
        await using StubJsonRpcServer server = new(_ => (HttpStatusCode.OK, responseBody));

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            MeasuredRequests = 3,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Succeeded, Is.EqualTo(success ? 3 : 0));
            Assert.That(results[0].RpcErrors, Is.EqualTo(success ? 0 : 3));
        }
    }

    [Test]
    public void Fails_on_a_batch_record_it_would_have_to_rewrite()
    {
        // A batch's entries carry their own block and fee fields the rewriter cannot reach, so sending
        // one as captured would silently break the every-request-hits-latest contract.
        const string batch = """[{"method":"eth_call","params":[{"to":"0x01"},"0x10",{}],"id":1,"jsonrpc":"2.0"}]""";
        string path = WriteTrace(".jsonl", [batch]);

        ReplayOptions options = new()
        {
            InputPath = path,
            Address = new Uri("http://127.0.0.1:1/"),
            Concurrencies = [1],
            MeasuredRequests = 0,
            WarmupRequests = 0,
        };

        ReplaySweep sweep = new(options, TextWriter.Null);

        Assert.That(
            async () => await sweep.RunAsync(CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("batch"));
    }

    [Test]
    public async Task Replays_a_batch_verbatim_when_no_edits_are_asked_for()
    {
        const string batch = """[{"method":"eth_call","params":[{"to":"0x01"},"0x10",{}],"id":1,"jsonrpc":"2.0"}]""";
        string path = WriteTrace(".jsonl", [batch]);
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            BlockTag = null,
            StripFeeFields = false,
            Concurrencies = [1],
            MeasuredRequests = 0,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Total, Is.EqualTo(1));
            Assert.That(server.Bodies[0], Is.EqualTo(batch));
        }
    }

    [Test]
    public async Task Never_counts_a_truncated_batch_as_a_success()
    {
        // The response scanner caps its buffer at one 8 MiB token; an error entry hiding behind a
        // giant result must not be reported as a success just because the scan could not reach it.
        string giant = new('a', 9 * 1024 * 1024);
        string body = $"[{{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"{giant}\"}},{{\"jsonrpc\":\"2.0\",\"id\":2,\"error\":{{\"code\":-32000,\"message\":\"boom\"}}}}]";
        string path = WriteTrace(".jsonl", Requests(2, _ => "latest"));
        await using StubJsonRpcServer server = new(_ => (HttpStatusCode.OK, body));

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            Concurrencies = [1],
            MeasuredRequests = 2,
            WarmupRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Succeeded, Is.Zero);
            Assert.That(results[0].RpcErrors, Is.EqualTo(2));
        }
    }

    [Test]
    public void Priming_gate_honours_cancellation_immediately()
    {
        // Ctrl+C during a starved priming burst must not ride out the gate's release timeout.
        using CancellationTokenSource cts = new();
        cts.Cancel();
        ReplaySweep.GatedContent.Gate gate = new(participants: 2);

        Assert.That(
            async () => await gate.WaitForBurstAsync(cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Warns_when_priming_or_warm_up_fails()
    {
        // Warm-up traffic is not reported in the results, so a node that failed it silently would
        // leave the measured window cold with nothing in the output to say so.
        string path = WriteTrace(".jsonl", Requests(20, _ => "latest"));
        await using StubJsonRpcServer server = new(_ => (HttpStatusCode.InternalServerError, "boom"));
        StringWriter log = new();

        ReplayOptions options = new()
        {
            InputPath = path,
            Address = server.Address,
            Concurrencies = [2],
            MeasuredRequests = 4,
            WarmupRequests = 2,
            StripFeeFields = false,
            Timeout = TimeSpan.FromSeconds(30),
        };

        await new ReplaySweep(options, log).RunAsync(CancellationToken.None);
        string output = log.ToString();

        // Progress is off, so these lines prove the warnings do not hide behind -p.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output, Does.Contain("priming failed 2/2"));
            Assert.That(output, Does.Contain("warm-up had 2/2 failures"));
        }
    }

    [Test]
    public async Task Dry_run_rewrites_without_sending_anything()
    {
        string path = WriteTrace(".jsonl.zst", Requests(25, index => index < 5 ? "latest" : $"0x{index:x}"));
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            DryRun = true,
            MeasuredRequests = 0,
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Requests, Is.Zero);
            Assert.That(results[0].Rewritten, Is.EqualTo(20));
            Assert.That(results[0].Succeeded, Is.EqualTo(25));
        }
    }

    [Test]
    public async Task Dry_run_accepts_an_omitted_optional_block_parameter()
    {
        // The node defaults an omitted block parameter to latest, so a single-parameter eth_call is
        // already at a "latest" target; failing it would reject a perfectly usable corpus.
        string path = WriteTrace(".jsonl", ["""{"method":"eth_call","params":[{"to":"0x01"}],"id":1,"jsonrpc":"2.0"}"""]);
        await using StubJsonRpcServer server = new();

        IReadOnlyList<LevelResult> results = await Run(server, path, options => options with
        {
            DryRun = true,
            MeasuredRequests = 0,
        });

        Assert.That(results[0].Succeeded, Is.EqualTo(1));
    }

    [Test]
    public void Dry_run_rejects_an_omitted_block_parameter_for_a_non_default_tag()
    {
        // With no slot to rewrite the node uses latest, so forcing any other tag cannot be honoured.
        string path = WriteTrace(".jsonl", ["""{"method":"eth_call","params":[{"to":"0x01"}],"id":1,"jsonrpc":"2.0"}"""]);

        ReplayOptions options = new()
        {
            InputPath = path,
            Address = new Uri("http://127.0.0.1:1/"),
            BlockTag = "0x10",
            Concurrencies = [1],
            DryRun = true,
            MeasuredRequests = 0,
        };

        Assert.That(
            async () => await new ReplaySweep(options, TextWriter.Null).RunAsync(CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains("omits"));
    }

    [Test]
    public void Dry_run_fails_loudly_on_a_record_it_cannot_rewrite()
    {
        // Silently replaying a record at the wrong block would make the whole run meaningless, so a
        // record without a block parameter has to stop the dry run rather than pass through it.
        string path = WriteTrace(".jsonl", ["""{"method":"eth_chainId","params":[],"id":1,"jsonrpc":"2.0"}"""]);

        ReplayOptions options = new()
        {
            InputPath = path,
            Address = new Uri("http://127.0.0.1:1/"),
            Concurrencies = [1],
            DryRun = true,
            MeasuredRequests = 0,
        };

        ReplaySweep sweep = new(options, TextWriter.Null);

        Assert.That(async () => await sweep.RunAsync(CancellationToken.None), Throws.InstanceOf<InvalidDataException>());
    }

    private static Task<IReadOnlyList<LevelResult>> Run(
        StubJsonRpcServer server,
        string path,
        Func<ReplayOptions, ReplayOptions> configure)
    {
        ReplayOptions options = configure(new ReplayOptions
        {
            InputPath = path,
            Address = server.Address,
            Concurrencies = [2],
            Timeout = TimeSpan.FromSeconds(30),
            // Off by default here so a test asserting on request bodies sees them as written.
            StripFeeFields = false,
        });

        return new ReplaySweep(options, TextWriter.Null).RunAsync(CancellationToken.None);
    }

    private static string[] SortedSlice(IReadOnlyList<string> bodies, int start, int count)
    {
        string[] slice = new string[count];
        for (int i = 0; i < count; i++)
        {
            slice[i] = bodies[start + i];
        }

        Array.Sort(slice, StringComparer.Ordinal);

        return slice;
    }

    private static IReadOnlyList<string> FeeBearingRequests(int count)
    {
        string[] records = new string[count];
        for (int i = 0; i < count; i++)
        {
            records[i] = $"{{\"method\":\"eth_call\",\"params\":[{{\"from\":\"0x{i:x2}\",\"gasPrice\":\"0x71afd498d0\",\"gas\":\"0x77359400\",\"data\":\"0xabcdef\"}},\"0x{i:x}\",{{}}],\"id\":{i},\"jsonrpc\":\"2.0\"}}";
        }

        return records;
    }

    private static IReadOnlyList<string> Requests(int count, Func<int, string> blockTag)
    {
        string[] records = new string[count];
        for (int i = 0; i < count; i++)
        {
            records[i] = $"{{\"method\":\"eth_call\",\"params\":[{{\"to\":\"0x{i:x2}\",\"data\":\"0xabcdef\"}},\"{blockTag(i)}\",{{}}],\"id\":{i},\"jsonrpc\":\"2.0\"}}";
        }

        return records;
    }

    private string WriteTrace(string extension, IReadOnlyList<string> records)
    {
        string path = Path.Combine(_directory, $"trace{extension}");
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', records) + '\n');

        using FileStream file = File.Create(path);
        if (Path.GetExtension(path) == ".zst")
        {
            using CompressionStream compressor = new(file);
            compressor.Write(bytes);
        }
        else
        {
            file.Write(bytes);
        }

        return path;
    }
}
