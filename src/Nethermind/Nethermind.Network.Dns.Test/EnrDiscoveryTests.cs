// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Dns.Test.EnrTreeCrawlerTests;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EnrDiscoveryTests
{
    [TestCase("all.mainnet.ethdisco.net")]
    [TestCase("enrtree://AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE@all.mainnet.ethdisco.net")]
    [Explicit("CI blocks DNS requests.")]
    public async Task Test_enr_discovery(string url)
    {
        NodeRecordSigner singer = new(new Ecdsa(), TestItem.PrivateKeyA);
        TestErrorLogManager testErrorLogManager = new();
        INetworkConfig config = new NetworkConfig();
        config.DiscoveryDns = url;
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.IsForkIdCompatible(Arg.Any<ForkId>()).Returns(true);
        EnrDiscovery enrDiscovery = new(new EnrRecordParser(singer), config, forkInfo, testErrorLogManager);
        long startTime = Stopwatch.GetTimestamp();
        List<Node> addedRecords = enrDiscovery.DiscoverNodes(default).ToBlockingEnumerable().ToList();

        await TestContext.Out.WriteLineAsync($"Actually added {addedRecords.Count} in {Stopwatch.GetElapsedTime(startTime):g}");
        foreach (TestErrorLogManager.Error error in testErrorLogManager.Errors)
        {
            await TestContext.Out.WriteLineAsync(error.Text);
        }
        Assert.That(addedRecords.Count, Is.EqualTo(3000));
    }

    [Test]
    [Explicit("CI blocks DNS requests.")]
    public async Task Test_enr_discovery2()
    {

        NodeRecordSigner singer = new(new Ecdsa(), TestItem.PrivateKeyA);
        EnrRecordParser parser = new(singer);
        EnrTreeCrawler crawler = new(LimboTraceLogger.Instance);
        int verified = 0;
        await foreach (string record in crawler.SearchTree("all.mainnet.ethdisco.net", default))
        {
            NodeRecord nodeRecord = parser.ParseRecord(record);
            Assert.That(nodeRecord.ToString(), Is.EqualTo(record));
            verified++;
        }

        await TestContext.Out.WriteLineAsync($"Verified {verified}");
    }

    [Test]
    public void Disabled_discovery_never_creates_a_dns_client()
    {
        int clients = 0;
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ =>
            {
                clients++;
                throw new InvalidOperationException("disabled discovery reached DNS");
            });
        EnrDiscovery discovery = new(new EnrRecordParser(new NodeRecordSigner(new Ecdsa())),
            new NetworkConfig { DiscoveryDns = null }, Substitute.For<IForkInfo>(), NullLogManager.Instance, crawler);
        List<Node> nodes = [];
        Assert.That(async () => nodes = await discovery.DiscoverNodes(default).ToListAsync(), Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Is.Empty);
            Assert.That(clients, Is.Zero);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Root_resolver_failure_retries_after_cooldown_for_the_original_consumer(CancellationToken token)
    {
        string record = CreateRecord().ToString();
        int rootQueries = 0;
        DelegateClient client = new((query, _) =>
        {
            if (query.Length == 0 && ++rootQueries == 1) throw new SocketException();
            return Task.FromResult<IEnumerable<string>>(query.Length == 0 ? [Root(Label(record))] : query == Label(record) ? [record] : []);
        });
        ManualTimeProvider time = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, new ManualTimeProvider());
        Task<List<Node>> original = discovery.DiscoverNodes(token).ToListAsync(token).AsTask();
        await WaitForCooldown(time.TimerCreated, original, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(time.DueTimes, Is.EqualTo(new[] { TimeSpan.FromMinutes(10) }));
            Assert.That(original.IsCompleted, Is.False);
        }
        time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await original.WaitAsync(token)).Select(node => node.Id), Is.EqualTo(new[] { TestItem.PublicKeyA }));
            Assert.That(rootQueries, Is.EqualTo(2));
        }
    }

    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    [CancelAfter(10000)]
    public async Task Original_consumer_resumes_only_interrupted_work_after_cooldown(int interruptions, bool joinDuringCooldown, CancellationToken token)
    {
        NodeRecord first = CreateRecord();
        NodeRecord second = CreateRecord(TestItem.PrivateKeyB);
        string firstText = first.ToString();
        string secondText = second.ToString();
        string branch = $"enrtree-branch:{Label(firstText)},{Label(secondText)}";
        List<string> queries = [];
        TaskCompletionSource[] started = Enumerable.Range(0, interruptions + 1).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        int attempts = 0;
        DelegateClient client = new(async (query, cancellation) =>
        {
            queries.Add(query);
            if (query == Label(secondText) && attempts++ < interruptions)
            {
                started[attempts - 1].TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return query switch
            {
                "" => [Root(Label(branch))],
                _ when query == Label(branch) => [branch],
                _ when query == Label(firstText) => [firstText],
                _ when query == Label(secondText) => [secondText],
                _ => []
            };
        });
        ManualTimeProvider time = new();
        ManualTimeProvider deadline = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, deadline);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using IAsyncEnumerator<Node> original = discovery.DiscoverNodes(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await original.MoveNextAsync(), Is.True);
            Assert.That(original.Current.Id, Is.EqualTo(TestItem.PublicKeyA));
        }
        await using IAsyncEnumerator<Node> joining = discovery.DiscoverNodes(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Task<bool>? joiningRemaining = null;
        Task<bool> remaining = original.MoveNextAsync().AsTask();
        for (int attempt = 0; attempt < interruptions; attempt++)
        {
            await WaitForQuery(started[attempt].Task, remaining, token);
            Task cooldown = time.NextTimerCreated;
            deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
            await WaitForCooldown(cooldown, remaining, token);
            Assert.That(time.DueTimes, Is.EqualTo(Enumerable.Repeat(TimeSpan.FromMinutes(10), attempt + 1)));
            if (joinDuringCooldown && attempt == 0)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(await joining.MoveNextAsync(), Is.True);
                    Assert.That(joining.Current.Id, Is.EqualTo(TestItem.PublicKeyA));
                }
                joiningRemaining = joining.MoveNextAsync().AsTask();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(remaining.IsCompleted, Is.False);
                Assert.That(attempts, Is.EqualTo(attempt + 1));
            }
            time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await remaining.WaitAsync(token), Is.True);
            Assert.That(original.Current.Id, Is.EqualTo(TestItem.PublicKeyB));
            Assert.That(await original.MoveNextAsync(), Is.False);
        }
        if (joiningRemaining is not null)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await joiningRemaining.WaitAsync(token), Is.True);
                Assert.That(joining.Current.Id, Is.EqualTo(TestItem.PublicKeyB));
                Assert.That(await joining.MoveNextAsync(), Is.False);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queries.Count(query => query.Length == 0), Is.EqualTo(1));
            Assert.That(queries.Count(query => query == Label(branch)), Is.EqualTo(1));
            Assert.That(queries.Count(query => query == Label(firstText)), Is.EqualTo(1));
            Assert.That(queries.Count(query => query == Label(secondText)), Is.EqualTo(interruptions + 1));
            Assert.That(attempts, Is.EqualTo(interruptions + 1));
            Assert.That(time.TimerCreated.IsCompleted, Is.EqualTo(interruptions != 0));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Resumed_crawl_keeps_lifetime_query_and_record_budgets([Values] bool records, CancellationToken token)
    {
        Dictionary<string, string[]> entries = BuildLargeTree(records);
        int queries = 0;
        TaskCompletionSource interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DelegateClient client = new(async (query, cancellation) =>
        {
            if (++queries == 2048)
            {
                interrupted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return entries.GetValueOrDefault(query, []);
        });
        ManualTimeProvider deadline = new();
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => client, timeProvider: deadline);
        EnrTreeCrawler.CrawlState state = crawler.CreateCrawl("example.test")!;
        Task<List<string>> first = crawler.SearchTree(state, token).ToListAsync(token).AsTask();
        await interrupted.Task.WaitAsync(token);
        deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
        List<string> before = await first;
        Assert.That(state.Paused, Is.True);
        List<string> after = await crawler.SearchTree(state, token).ToListAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.Count + after.Count, Is.EqualTo(records ? 4096 : 0));
            Assert.That(before.Intersect(after), Is.Empty);
            Assert.That(queries, Is.EqualTo(records ? 4150 : 16384));
            Assert.That(state.Paused, Is.False);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Resumed_root_answers_keep_the_lifetime_validation_budget([Values(1024, 2048)] int malformedAnswers, CancellationToken token)
    {
        string record = CreateRecord().ToString();
        ManualTimeProvider deadline = new();
        int rootQueries = 0;
        DelegateClient client = new((query, _) => Task.FromResult<IEnumerable<string>>(
            query.Length == 0 ? Answers(++rootQueries < 8) : query == Label(record) ? [record] : []));
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => client, timeProvider: deadline);
        EnrTreeCrawler.CrawlState state = crawler.CreateCrawl("example.test")!;
        for (int attempt = 0; attempt < 7; attempt++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await crawler.SearchTree(state, token).ToListAsync(token), Is.Empty);
                Assert.That(state.Paused, Is.True);
            }
        }
        List<string> resumed = await crawler.SearchTree(state, token).ToListAsync(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootQueries, Is.EqualTo(8));
            Assert.That(resumed, Is.EqualTo(malformedAnswers == 2048 ? Array.Empty<string>() : new[] { record }));
            Assert.That(state.Paused, Is.False);
        }

        IEnumerable<string> Answers(bool interrupt)
        {
            for (int answer = 0; answer < malformedAnswers; answer++) yield return "malformed";
            if (interrupt) deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
            yield return Root(Label(record));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Deadline_during_answers_retries_only_unfinished_query_without_duplicate_records(CancellationToken token)
    {
        string first = CreateRecord().ToString();
        string second = CreateRecord(TestItem.PrivateKeyB).ToString();
        string branch = $"enrtree-branch:{Label(first)},{Label(second)}";
        ManualTimeProvider time = new();
        ManualTimeProvider deadline = new();
        List<string> queries = [];
        int attempts = 0;
        DelegateClient client = new((query, _) =>
        {
            queries.Add(query);
            IEnumerable<string> answers = query switch
            {
                "" => [Root(Label(branch))],
                _ when query == Label(branch) => [branch],
                _ when query == Label(first) => ++attempts == 1 ? InterruptedAnswers() : [first],
                _ when query == Label(second) => [second],
                _ => []
            };
            return Task.FromResult(answers);
        });
        Task cooldown = time.NextTimerCreated;
        EnrDiscovery discovery = CreateDiscovery(client, time, deadline);
        await using IAsyncEnumerator<Node> original = discovery.DiscoverNodes(token).GetAsyncEnumerator(token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await original.MoveNextAsync(), Is.True);
            Assert.That(original.Current.Id, Is.EqualTo(TestItem.PublicKeyA));
        }
        Task<bool> remaining = original.MoveNextAsync().AsTask();
        await WaitForCooldown(cooldown, remaining, token);
        Assert.That(remaining.IsCompleted, Is.False);
        time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await remaining.WaitAsync(token), Is.True);
            Assert.That(original.Current.Id, Is.EqualTo(TestItem.PublicKeyB));
            Assert.That(await original.MoveNextAsync(), Is.False);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queries.Count(query => query.Length == 0), Is.EqualTo(1));
            Assert.That(queries.Count(query => query == Label(first)), Is.EqualTo(2));
            Assert.That(queries.Count(query => query == Label(second)), Is.EqualTo(1));
        }

        IEnumerable<string> InterruptedAnswers()
        {
            yield return first;
            deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
            yield return first;
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Last_consumer_cancellation_allows_a_later_crawl_to_restart([Values] bool duringCooldown, CancellationToken token)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken[] resolverCancellations = new CancellationToken[2];
        int queries = 0;
        DelegateClient client = new(async (_, cancellation) =>
        {
            int attempt = Interlocked.Increment(ref queries);
            resolverCancellations[attempt - 1] = cancellation;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return [];
        });
        ManualTimeProvider time = new();
        ManualTimeProvider deadline = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, deadline);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> original = discovery.DiscoverNodes(cancellation.Token).ToListAsync().AsTask();
        await started.Task.WaitAsync(token);
        if (duringCooldown)
        {
            Task cooldown = time.NextTimerCreated;
            deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
            await WaitForCooldown(cooldown, original, token);
        }
        cancellation.Cancel();
        Assert.That(async () => await original, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(resolverCancellations[0].IsCancellationRequested, Is.True, "The original DNS query was not cancelled before its consumer finished.");
        time.AdvanceAndFireTimer(TimeSpan.FromHours(1));
        Assert.That(queries, Is.EqualTo(1));
        using CancellationTokenSource retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> retry = discovery.DiscoverNodes(retryCancellation.Token).ToListAsync().AsTask();
        Assert.That(queries, Is.EqualTo(2));
        retryCancellation.Cancel();
        Assert.That(async () => await retry, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(resolverCancellations[1].IsCancellationRequested, Is.True, "The restarted DNS query was not cancelled before its consumer finished.");
    }

    [Test]
    public async Task Two_consumers_share_one_crawl_and_receive_every_node([Values] bool cancelFirst)
    {
        NodeRecord record = CreateRecord();
        GatedClient client = new(record.ToString());
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => client);
        EnrDiscovery discovery = new(new EnrRecordParser(new NodeRecordSigner(new Ecdsa())),
            new NetworkConfig { DiscoveryDns = "example.test" }, Substitute.For<IForkInfo>(), NullLogManager.Instance, crawler);
        using CancellationTokenSource firstCancellation = new();
        Task<List<Node>> first = discovery.DiscoverNodes(firstCancellation.Token).ToListAsync().AsTask();
        Task<List<Node>> second = discovery.DiscoverNodes(default).ToListAsync().AsTask();
        if (cancelFirst)
        {
            firstCancellation.Cancel();
            Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        }
        client.Release.TrySetResult();
        List<Node> nodes = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(nodes.Select(n => n.Id), Is.EqualTo(new[] { TestItem.PublicKeyA }));
        if (!cancelFirst) Assert.That((await first).Select(n => n.Id), Is.EqualTo(nodes.Select(n => n.Id)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await discovery.DiscoverNodes(default).ToListAsync(), Has.Count.EqualTo(1));
            Assert.That(client.RootQueries, Is.EqualTo(1));
        }
    }

    [Test]
    public void Enr_padding_uses_complete_quartets([Values(0, 2, 3)] int remainder)
    {
        NodeRecord record = CreateRecord();
        NodeRecordSigner signer = new(new Ecdsa(), TestItem.PrivateKeyA);
        for (ulong sequence = 0; ; sequence++)
        {
            Assert.That(sequence, Is.LessThan(65536UL));
            record.EnrSequence = sequence;
            signer.Sign(record);
            if ((record.ToString().Length - 4) % 4 == remainder) break;
        }
        Assert.That(new EnrRecordParser(signer).ParseRecord(record.ToString()).ToString(), Is.EqualTo(record.ToString()));
    }

    [Test]
    public void Impossible_base64_remainder_is_rejected() =>
        Assert.That(() => new EnrRecordParser(new NodeRecordSigner(new Ecdsa())).ParseRecord("enr:A"), Throws.TypeOf<FormatException>());

    [Test]
    [CancelAfter(10000)]
    public Task Lookup_exception_retries_the_original_label_and_preserves_siblings(CancellationToken token)
        => UnexpectedResolverFailure(false, token);

    [Test]
    [CancelAfter(10000)]
    public Task Lazy_answer_fault_resumes_a_new_enumerator_without_duplicate_prefix(CancellationToken token)
        => UnexpectedResolverFailure(true, token);

    private static async Task UnexpectedResolverFailure(bool lazy, CancellationToken token)
    {
        string first = CreateRecord().ToString();
        string sibling = CreateRecord(TestItem.PrivateKeyB).ToString();
        string branch = $"enrtree-branch:{Label(first)},{Label(sibling)}";
        int attempts = 0;
        DelegateClient client = new((query, _) =>
        {
            if (query == Label(first) && ++attempts == 1)
            {
                if (!lazy) throw new InvalidOperationException("resolver failure");
                return Task.FromResult(FaultedAnswers());
            }
            return Task.FromResult<IEnumerable<string>>(query switch
            {
                "" => [Root(Label(branch))],
                _ when query == Label(branch) => [branch],
                _ when query == Label(first) => [first],
                _ when query == Label(sibling) => [sibling],
                _ => []
            });
        });
        ManualTimeProvider time = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, new ManualTimeProvider());
        Task cooldown = time.NextTimerCreated;
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> result = discovery.DiscoverNodes(cleanup.Token).ToListAsync(cleanup.Token).AsTask();
        try
        {
            await WaitForCooldown(cooldown, result, token);
            Task repeatedCooldown = time.NextTimerCreated;
            time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
            Assert.That(await Task.WhenAny(result, repeatedCooldown).WaitAsync(token), Is.SameAs(result), "Recovery must use a fresh enumerator rather than pause again on the faulted iterator.");
            List<Node> found = await result.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found.Select(node => node.Id), Is.EqualTo(new[] { TestItem.PublicKeyA, TestItem.PublicKeyB }));
                Assert.That(attempts, Is.EqualTo(2));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)result).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        IEnumerable<string> FaultedAnswers()
        {
            yield return first;
            throw new InvalidOperationException("lazy resolver failure");
        }
    }

    /// <remarks>Three failures abandon a possibly honest non-root label after two cooldowns; siblings continue.</remarks>
    [Test]
    [CancelAfter(10000)]
    public async Task Three_resolver_failures_skip_only_the_failed_label([Values] bool deadlineFailure, CancellationToken token)
    {
        string first = CreateRecord().ToString();
        string failing = CreateRecord(TestItem.PrivateKeyB).ToString();
        string sibling = CreateRecord(TestItem.PrivateKeyC).ToString();
        string branch = $"enrtree-branch:{Label(first)},{Label(failing)},{Label(sibling)}";
        TaskCompletionSource[] started = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        TaskCompletionSource[] failed = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        TaskCompletionSource siblingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> queries = [];
        int attempts = 0;
        DelegateClient client = new(async (query, cancellation) =>
        {
            queries.Add(query);
            if (query == Label(failing))
            {
                int attempt = Math.Min(attempts++, 3);
                started[attempt].TrySetResult();
                if (deadlineFailure) await Task.Delay(Timeout.Infinite, cancellation);
                else await failed[attempt].Task.WaitAsync(cancellation);
                throw new InvalidOperationException("unavailable leaf");
            }
            if (query == Label(sibling)) siblingStarted.TrySetResult();
            return query switch
            {
                "" => [Root(Label(branch))],
                _ when query == Label(branch) => [branch],
                _ when query == Label(first) => [first],
                _ when query == Label(sibling) => [sibling],
                _ => []
            };
        });
        ManualTimeProvider time = new();
        ManualTimeProvider deadline = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, deadline);
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using IAsyncEnumerator<Node> consumer = discovery.DiscoverNodes(cleanup.Token).GetAsyncEnumerator(cleanup.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await consumer.MoveNextAsync(), Is.True);
            Assert.That(consumer.Current.Id, Is.EqualTo(TestItem.PublicKeyA));
        }
        Task<bool> remaining = consumer.MoveNextAsync().AsTask();
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await WaitForQuery(started[attempt].Task, remaining, token);
                Task cooldown = time.NextTimerCreated;
                if (deadlineFailure) deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
                else failed[attempt].TrySetResult();
                if (attempt < 2)
                {
                    await WaitForCooldown(cooldown, remaining, token);
                    time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
                }
                else
                {
                    Task outcome = await Task.WhenAny(siblingStarted.Task, remaining, cooldown).WaitAsync(token);
                    bool immediateSibling = siblingStarted.Task.IsCompleted;
                    if (ReferenceEquals(outcome, cooldown) && !immediateSibling)
                    {
                        time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
                        await Task.WhenAny(started[3].Task, remaining).WaitAsync(token);
                    }
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(attempts, Is.EqualTo(3));
                        Assert.That(immediateSibling, Is.True, "An exhausted leaf must dispatch its sibling without another cooldown.");
                    }
                }
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await remaining.WaitAsync(token), Is.True);
                Assert.That(consumer.Current.Id, Is.EqualTo(TestItem.PublicKeyC));
                Assert.That(await consumer.MoveNextAsync(), Is.False);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queries.Count(query => query == Label(first)), Is.EqualTo(1));
                Assert.That(queries.Count(query => query == Label(sibling)), Is.EqualTo(1));
                Assert.That(queries.Count(query => query == Label(branch)), Is.EqualTo(1));
                Assert.That(queries.Count(query => query.Length == 0), Is.EqualTo(1));
                Assert.That(time.DueTimes, Has.Count.EqualTo(2));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)remaining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Root_interruptions_resume_until_recovery_or_caller_cancellation([Values] bool alwaysTimesOut, CancellationToken token)
    {
        string record = CreateRecord().ToString();
        TaskCompletionSource[] started = Enumerable.Range(0, 7).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        int attempts = 0;
        DelegateClient client = new(async (query, cancellation) =>
        {
            if (query.Length == 0)
            {
                int attempt = attempts++;
                started[Math.Min(attempt, 6)].TrySetResult();
                if (attempt < 6 || alwaysTimesOut) await Task.Delay(Timeout.Infinite, cancellation);
                return [Root(Label(record))];
            }
            return query == Label(record) ? [record] : [];
        });
        ManualTimeProvider time = new();
        ManualTimeProvider deadline = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, deadline);
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> result = discovery.DiscoverNodes(cleanup.Token).ToListAsync(cleanup.Token).AsTask();
        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await WaitForQuery(started[attempt].Task, result, token);
                Task cooldown = time.NextTimerCreated;
                deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
                await WaitForCooldown(cooldown, result, token);
                time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
            }
            await Task.WhenAny(started[6].Task, result).WaitAsync(token);
            if (alwaysTimesOut)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(attempts, Is.EqualTo(7));
                    Assert.That(result.IsCompleted, Is.False);
                }
                await cleanup.CancelAsync();
                Assert.That(async () => await result, Throws.InstanceOf<OperationCanceledException>());
            }
            else Assert.That((await result.WaitAsync(token)).Select(node => node.Id), Is.EqualTo(new[] { TestItem.PublicKeyA }));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(attempts, Is.EqualTo(7));
                Assert.That(time.DueTimes, Has.Count.EqualTo(6));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)result).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Later_enumeration_restarts_after_a_local_producer_fault()
    {
        string record = CreateRecord().ToString();
        int clients = 0;
        DelegateClient client = new((query, _) => Task.FromResult<IEnumerable<string>>(query.Length == 0 ? [Root(Label(record))] : query == Label(record) ? [record] : []));
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => ++clients == 1 ? throw new InvalidOperationException("client creation failed") : client);
        EnrDiscovery discovery = new(new EnrRecordParser(new NodeRecordSigner(new Ecdsa())), new NetworkConfig { DiscoveryDns = "example.test" }, Substitute.For<IForkInfo>(), NullLogManager.Instance, crawler);
        Assert.That(async () => await discovery.DiscoverNodes(default).ToListAsync(), Throws.TypeOf<InvalidOperationException>());
        List<Node> recovered = [];
        Assert.That(async () => recovered = await discovery.DiscoverNodes(default).ToListAsync(), Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.Select(node => node.Id), Is.EqualTo(new[] { TestItem.PublicKeyA }));
            Assert.That(clients, Is.EqualTo(2));
        }
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Escaping_failures_and_queries_share_one_lifetime_allowance([Values(EnrTreeCrawler.MaxQueries - 1, EnrTreeCrawler.MaxQueries)] int faultAt, CancellationToken token)
    {
        Dictionary<string, string[]> entries = BuildLargeTree(false);
        int queries = 0;
        DelegateClient client = new((query, _) => Task.FromResult(++queries == faultAt ? FaultedAnswers() : entries.GetValueOrDefault(query, [])));
        ManualTimeProvider time = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, new ManualTimeProvider());
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> result = discovery.DiscoverNodes(cleanup.Token).ToListAsync(cleanup.Token).AsTask();
        try
        {
            Task outcome = await Task.WhenAny(result, time.TimerCreated).WaitAsync(token);
            bool terminal = ReferenceEquals(outcome, result);
            if (!terminal)
            {
                time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
                await result.WaitAsync(token);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queries, Is.EqualTo(faultAt));
                Assert.That(terminal, Is.True, "The final lifetime work unit must not schedule recovery.");
                Assert.That(time.DueTimes, Is.Empty);
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)result).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        static IEnumerable<string> FaultedAnswers()
        {
            yield return Fail();
            static string Fail() => throw new InvalidOperationException("lazy answer failure");
        }
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Prequery_faults_share_the_lifetime_allowance_with_later_queries([Values(2, EnrTreeCrawler.MaxQueries)] int failures, CancellationToken token)
    {
        string record = CreateRecord().ToString();
        int queries = 0;
        DelegateClient client = new((query, _) =>
        {
            queries++;
            return Task.FromResult<IEnumerable<string>>(query.Length == 0 ? [Root(Label(record))] : query == Label(record) ? [record] : []);
        });
        ManualTimeProvider time = new();
        DeadlineFaultProvider deadline = new(failures);
        EnrDiscovery discovery = CreateDiscovery(client, time, deadline);
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> result = discovery.DiscoverNodes(cleanup.Token).ToListAsync(cleanup.Token).AsTask();
        try
        {
            Task cooldown = time.TimerCreated;
            for (int attempt = 1; attempt <= failures; attempt++)
            {
                if (attempt == failures && failures == EnrTreeCrawler.MaxQueries)
                {
                    Task outcome = await Task.WhenAny(result, cooldown).WaitAsync(token);
                    bool terminal = ReferenceEquals(outcome, result);
                    if (!terminal) time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(deadline.Attempts, Is.EqualTo(EnrTreeCrawler.MaxQueries));
                        Assert.That(terminal, Is.True, "Exhausted pre-query work must terminate without another timer.");
                        Assert.That(queries, Is.Zero);
                    }
                    break;
                }
                await WaitForCooldown(cooldown, result, token);
                Task nextCooldown = time.NextTimerCreated;
                time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
                cooldown = nextCooldown;
            }
            List<Node> found = await result.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(found.Select(node => node.Id), Is.EqualTo(failures == 2 ? new[] { TestItem.PublicKeyA } : Array.Empty<Nethermind.Core.Crypto.PublicKey>()));
                Assert.That(queries, Is.EqualTo(failures == 2 ? 3 : 0));
                Assert.That(time.DueTimes, Has.Count.EqualTo(failures == 2 ? 2 : EnrTreeCrawler.MaxQueries - 1));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)result).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Caught_lookup_failures_consume_only_their_existing_query_unit(CancellationToken token)
    {
        Dictionary<string, string[]> entries = BuildLargeTree(false);
        int queries = 0;
        DelegateClient client = new((query, _) =>
        {
            if (++queries == EnrTreeCrawler.MaxQueries - 1) throw new InvalidOperationException("resolver failure");
            return Task.FromResult<IEnumerable<string>>(entries.GetValueOrDefault(query, []));
        });
        ManualTimeProvider time = new();
        EnrDiscovery discovery = CreateDiscovery(client, time, new ManualTimeProvider());
        using CancellationTokenSource cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<Node>> result = discovery.DiscoverNodes(cleanup.Token).ToListAsync(cleanup.Token).AsTask();
        try
        {
            Assert.That(await Task.WhenAny(result, time.TimerCreated).WaitAsync(token), Is.SameAs(time.TimerCreated), "A caught failed lookup already consumed its query unit and may use the remaining unit.");
            time.AdvanceAndFireTimer(TimeSpan.FromMinutes(10));
            await result.WaitAsync(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(queries, Is.EqualTo(EnrTreeCrawler.MaxQueries));
                Assert.That(time.DueTimes, Has.Count.EqualTo(1));
            }
        }
        finally
        {
            await cleanup.CancelAsync();
            await ((Task)result).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class DeadlineFaultProvider(int failures) : TimeProvider
    {
        private readonly ManualTimeProvider _clock = new();
        internal int Attempts;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (++Attempts <= failures) throw new InvalidOperationException("deadline timer creation failed");
            return _clock.CreateTimer(callback, state, dueTime, period);
        }
    }

    internal static NodeRecord CreateRecord(PrivateKey? privateKey = null)
    {
        privateKey ??= TestItem.PrivateKeyA;
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(privateKey.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Loopback));
        record.SetEntry(new TcpEntry(30303));
        new NodeRecordSigner(new Ecdsa(), privateKey).Sign(record);
        return record;
    }

    private static async Task WaitForCooldown(Task cooldown, Task consumer, CancellationToken token)
    {
        Task completed = await Task.WhenAny(cooldown, consumer).WaitAsync(token);
        Assert.That(completed, Is.SameAs(cooldown), "The consumer finished before the interrupted crawl scheduled its cooldown.");
        await cooldown.WaitAsync(token);
    }

    private static async Task WaitForQuery(Task started, Task consumer, CancellationToken token)
    {
        Task completed = await Task.WhenAny(started, consumer).WaitAsync(token);
        Assert.That(completed, Is.SameAs(started), "The consumer finished before the interrupted query was retried.");
        await started.WaitAsync(token);
    }

    private static EnrDiscovery CreateDiscovery(IDnsClient client, ManualTimeProvider time, TimeProvider deadline) => new(
        new EnrRecordParser(new NodeRecordSigner(new Ecdsa())), new NetworkConfig { DiscoveryDns = "example.test" },
        Substitute.For<IForkInfo>(), NullLogManager.Instance, new EnrTreeCrawler(NullLogger.Instance, _ => client, timeProvider: deadline))
    { TimeProvider = time };

    internal sealed class DelegateClient(Func<string, CancellationToken, Task<IEnumerable<string>>> lookup) : IDnsClient
    {
        public Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default) => lookup(query, cancellationToken);
    }

    private sealed class GatedClient(string record) : IDnsClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RootQueries;
        public async Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default)
        {
            string label = EnrTreeCrawlerTests.Label(record);
            if (query.Length == 0)
            {
                Interlocked.Increment(ref RootQueries);
                await Release.Task.WaitAsync(cancellationToken);
                return [EnrTreeCrawlerTests.Root(label)];
            }
            return query == label ? [record] : [];
        }
    }
}
