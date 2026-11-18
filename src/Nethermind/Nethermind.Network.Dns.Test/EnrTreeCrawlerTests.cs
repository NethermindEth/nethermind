// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Crypto;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EnrTreeCrawlerTests
{
    [Test]
    public async Task Repeated_enumeration_uses_independent_crawl_state([Values] bool interleaved)
    {
        const string record = "enr:first";
        FakeDnsClient client = new(new Dictionary<string, string[]>
        {
            [""] = [Root(Label(record))],
            [Label(record)] = [record]
        });
        EnrTreeCrawler crawler = new(LimboLogs.Instance.GetClassLogger<EnrTreeCrawler>(), _ => client);
        IAsyncEnumerable<string> records = crawler.SearchTree("example.test");
        await using IAsyncEnumerator<string> first = records.GetAsyncEnumerator();
        await using IAsyncEnumerator<string> second = records.GetAsyncEnumerator();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await first.MoveNextAsync(), Is.True);
            Assert.That(first.Current, Is.EqualTo(record));
        }
        if (!interleaved) Assert.That(await first.MoveNextAsync(), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await second.MoveNextAsync(), Is.True);
            Assert.That(second.Current, Is.EqualTo(record));
            Assert.That(await second.MoveNextAsync(), Is.False);
        }
        if (interleaved) Assert.That(await first.MoveNextAsync(), Is.False);
        Assert.That(client.Queries, Is.EquivalentTo(new[] { "", Label(record), EmptyLinkRoot, "", Label(record), EmptyLinkRoot }));
    }

    [Test]
    public async Task Resolver_timeouts_pause_or_skip_without_losing_sibling_work([Values("cancellation", "socket", "dns")] string failure)
    {
        const string first = "enr:first";
        const string sibling = "enr:sibling";
        string branch = $"enrtree-branch:{Label(first)},{Label(sibling)}";
        ILookupClient lookup = Substitute.For<ILookupClient>();
        lookup.NameServers.Returns(new[] { new NameServer(IPAddress.Loopback) });
        int failures = 0;
        lookup.QueryAsync(Arg.Any<DnsQuestion>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string name = call.Arg<DnsQuestion>().QueryName.ToString().TrimEnd('.');
            string query = name == "example.test" ? "" : name[..^".example.test".Length];
            if (query == Label(first) && failures++ == 0)
            {
                Exception error = failure switch
                {
                    "cancellation" => new OperationCanceledException(),
                    "socket" => new SocketException((int)SocketError.TimedOut),
                    _ => new DnsResponseException("resolver timeout")
                };
                return Task.FromException<IDnsQueryResponse>(error);
            }
            string[] answers = query switch
            {
                "" => [Root(Label(branch))],
                _ when query == Label(branch) => [branch],
                _ when query == Label(first) => [first],
                _ when query == Label(sibling) => [sibling],
                _ => []
            };
            IDnsQueryResponse response = Substitute.For<IDnsQueryResponse>();
            response.Answers.Returns(answers.Select(text => (DnsResourceRecord)new global::DnsClient.Protocol.TxtRecord(
                new ResourceRecordInfo(name, ResourceRecordType.TXT, QueryClass.IN, 60, text.Length), [text], [text])).ToArray());
            return Task.FromResult(response);
        });
        TestLogger logger = new();
        DnsClient client = new("example.test", lookup);
        EnrTreeCrawler crawler = new(new ILogger(logger), _ => client);
        EnrTreeCrawler.CrawlState state = crawler.CreateCrawl("example.test")!;
        List<string> records = await crawler.SearchTree(state, default).ToListAsync();
        if (state.Paused)
        {
            Assert.That(state.Paused, Is.True);
            records.AddRange(await crawler.SearchTree(state, default).ToListAsync());
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.EqualTo(new[] { first, sibling }));
            Assert.That(failures, Is.EqualTo(2));
            Assert.That(state.Paused, Is.False);
            Assert.That(logger.LogList, Has.Some.Contains("DNS query"));
        }
    }

    private const string Signature = "CNoJofW_lNh7QFQkaVGhEX2ifbEZ3UkiBQCVyZCkM_I-72cEh8Bfd21cSS9BP5tyAqWF3jMVov8duUCdSByEQAE";
    private const string EmptyLinkRoot = "FDXN3SN67NA5DKA4J2GOK7BVQI";

    private const string EthDiscoSigner = "AKA3AM6LPBYEUDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE";
    private const string MainnetRoot = "enrtree-root:v1 e=PL443R7BHELRFKXORE3EXM23YY l=FDXN3SN67NA5DKA4J2GOK7BVQI seq=1790946901 sig=_5woNhIQQlLQwyA40HWId-mLyqBEnNTKqjZmVREq3v4xAM4NiDzaRM2sezSX1OcFLHiGB-FjUDa9YZz6DJgcQAE";
    private const string SepoliaRoot = "enrtree-root:v1 e=CJ45T74P3UEQL3ZLJDZECBG7MA l=FDXN3SN67NA5DKA4J2GOK7BVQI seq=1790946901 sig=AJdVwQHHi1RLMWcTeOadkgvls3Jgce2bqOcv07lnDAwB1FQS-ZvVIZiisGU_b_gBtdPmcj3udyZ-3SDmquoWXQE";

    private const string BranchLabel = "SNIGOIP7I67HGIGFKVFYHSSDYM";
    private const string BranchText = "enrtree-branch:LK4XPBOGFJGFAZQU5EDM5QRCPI,ISE6C2QIK4C66JK4Z47XCKUZCI";
    private const string FirstChildLabel = "LK4XPBOGFJGFAZQU5EDM5QRCPI";
    private const string SecondChildLabel = "ISE6C2QIK4C66JK4Z47XCKUZCI";

    private const string LeafLabel = "SXGMIVARLODNCEZQIWPQ46AGIM";
    private const string LeafText = "enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";

    [Test]
    public async Task Leaf_that_hashes_to_its_subdomain_is_returned()
    {
        FakeDnsClient client = new(new Dictionary<string, string[]>
        {
            [""] = [RootPointingAt(LeafLabel)],
            [LeafLabel] = [LeafText],
        });

        (List<string> records, TestLogger logger) = await Crawl(client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.EqualTo(new[] { LeafText }));
            Assert.That(logger.LogList, Has.None.Contains("does not hash"));
        }
    }

    [Test]
    public async Task Tampered_leaf_served_at_a_valid_subdomain_is_rejected_and_logged()
    {
        const string tamperedLeaf = "enr:-IS4QHCZrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";

        FakeDnsClient client = new(new Dictionary<string, string[]>
        {
            [""] = [RootPointingAt(LeafLabel)],
            [LeafLabel] = [tamperedLeaf],
        });

        (List<string> records, TestLogger logger) = await Crawl(client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.Empty);
            Assert.That(logger.LogList, Has.Some.Contains("does not hash to the subdomain"));
        }
    }

    [Test]
    public async Task Branch_that_hashes_to_its_subdomain_has_its_children_visited()
    {
        FakeDnsClient client = new(new Dictionary<string, string[]>
        {
            [""] = [RootPointingAt(BranchLabel)],
            [BranchLabel] = [BranchText],
        });

        (_, TestLogger logger) = await Crawl(client);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.Queries, Is.SupersetOf(new[] { FirstChildLabel, SecondChildLabel }));
            Assert.That(logger.LogList, Has.None.Contains("does not hash"));
        }
    }

    [Test]
    public async Task Tampered_branch_children_are_never_visited()
    {
        const string tamperedBranch = "enrtree-branch:LK4XPBOGFJGFAZQU5EDM5QRCPI,AAE6C2QIK4C66JK4Z47XCKUZCI";

        FakeDnsClient client = new(new Dictionary<string, string[]>
        {
            [""] = [RootPointingAt(BranchLabel)],
            [BranchLabel] = [tamperedBranch],
        });

        (_, TestLogger logger) = await Crawl(client);

        Assert.That(client.Queries, Has.None.EqualTo(FirstChildLabel));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.Queries, Has.None.EqualTo("AAE6C2QIK4C66JK4Z47XCKUZCI"));
            Assert.That(logger.LogList, Has.Some.Contains("does not hash to the subdomain"));
        }
    }

    private static string RootPointingAt(string enrRoot) =>
        $"enrtree-root:v1 e={enrRoot} l={EmptyLinkRoot} seq=1 sig={Signature}";

    [TestCase(MainnetRoot, "PL443R7BHELRFKXORE3EXM23YY")]
    [TestCase(SepoliaRoot, "CJ45T74P3UEQL3ZLJDZECBG7MA")]
    public async Task Root_signed_by_the_configured_signer_is_followed(string root, string enrRoot)
    {
        FakeDnsClient client = new(new Dictionary<string, string[]> { [""] = [root] });

        (_, TestLogger logger) = await Crawl(client, EthDiscoSigner);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(client.Queries, Does.Contain(enrRoot));
            Assert.That(logger.LogList, Has.None.Contains("not an enrtree-root signed"));
        }
    }

    [TestCase("enrtree-root:v1 e=PL443R7BHELRFKXORE3EXM23YY l=FDXN3SN67NA5DKA4J2GOK7BVQI seq=1790946902 sig=_5woNhIQQlLQwyA40HWId-mLyqBEnNTKqjZmVREq3v4xAM4NiDzaRM2sezSX1OcFLHiGB-FjUDa9YZz6DJgcQAE", EthDiscoSigner, TestName = "Tampered root content")]
    [TestCase(MainnetRoot, "AKA3AM6LPBYEVDMVNU3BSVQJ5AD45Y7YPOHJLEF6W26QOE4VTUDPE", TestName = "Root signed by another key")]
    [TestCase(LeafText, EthDiscoSigner, TestName = "Leaf served in place of the root")]
    public async Task Root_not_signed_by_the_configured_signer_is_rejected(string root, string signer)
    {
        FakeDnsClient client = new(new Dictionary<string, string[]> { [""] = [root] });

        (List<string> records, TestLogger logger) = await Crawl(client, signer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.Empty);
            Assert.That(client.Queries, Is.EqualTo(new[] { "" }));
            Assert.That(logger.LogList, Has.Some.Contains("not an enrtree-root signed by the configured tree signer"));
        }
    }

    [Test]
    public async Task Malformed_signer_key_skips_the_tree()
    {
        TestLogger logger = new();
        EnrTreeCrawler crawler = new(new ILogger(logger));
        List<string> records = [];
        await foreach (string record in crawler.SearchTree("enrtree://NOT-A-KEY@all.mainnet.ethdisco.net"))
        {
            records.Add(record);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.Empty);
            Assert.That(logger.LogList, Has.Some.Contains("is not a base32 compressed public key"));
        }
    }

    private static async Task<(List<string> Records, TestLogger Logger)> Crawl(FakeDnsClient client, string? signer = null)
    {
        TestLogger logger = new();
        EnrTreeCrawler crawler = new(new ILogger(logger), _ => client);
        string address = signer is null ? "example.test" : $"enrtree://{signer}@example.test";
        List<string> records = [];
        await foreach (string record in crawler.SearchTree(address))
        {
            records.Add(record);
        }

        return (records, logger);
    }

    private sealed class FakeDnsClient(Dictionary<string, string[]> records) : IDnsClient
    {
        public List<string> Queries { get; } = [];

        public Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult<IEnumerable<string>>(records.TryGetValue(query, out string[]? found) ? found : []);
        }
    }

    private const string Leaf = "enr:example";
    private const string EmptyLink = "FDXN3SN67NA5DKA4J2GOK7BVQI";

    [Test]
    public async Task Budget_truncation_warns_once_with_counts([Values] bool records)
    {
        Dictionary<string, string[]> entries = BuildLargeTree(records);
        TestLogger warnings = new() { IsWarn = true, IsInfo = false, IsDebug = false, IsTrace = false, IsError = false };
        EnrTreeCrawler crawler = new(new ILogger(warnings), _ => new CountingClient(entries));
        List<string> found = await crawler.SearchTree("example.test").ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found.Count, Is.EqualTo(records ? 4096 : 0));
            Assert.That(warnings.LogList.Count(message => message.Contains("truncated")), Is.EqualTo(1));
            Assert.That(warnings.LogList, Has.Count.EqualTo(2));
            Assert.That(warnings.LogList[0], Does.Contain("No ENR tree signer public key configured"));
        }
        Assert.That(warnings.LogList[1], Does.Contain("truncated").And.Contain(records ? "4096" : "16384"));
    }

    [Test]
    public async Task Sequence_cache_eviction_allows_new_trees_and_retains_recent_rollback_protection()
    {
        Dictionary<string, string[]> entries = new()
        {
            [string.Empty] = [SignedRoot(Label(Leaf), EmptyLink, TestItem.PrivateKeyA, 2)],
            [Label(Leaf)] = [Leaf]
        };
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => new CountingClient(entries));
        static string Address(int index) => $"enrtree://{Base32(TestItem.PrivateKeyA.CompressedPublicKey.Bytes)}@tree{index}.test";
        for (int index = 0; index < 33; index++)
            Assert.That(await crawler.SearchTree(Address(index)).ToListAsync(), Is.EqualTo(new[] { Leaf }));
        entries[string.Empty] = [SignedRoot(Label(Leaf), EmptyLink, TestItem.PrivateKeyA, 1)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await crawler.SearchTree(Address(1)).ToListAsync(), Is.Empty);
            Assert.That(await crawler.SearchTree(Address(32)).ToListAsync(), Is.Empty);
            Assert.That(await crawler.SearchTree(Address(0)).ToListAsync(), Is.EqualTo(new[] { Leaf }));
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Linked_tree_rotation_is_bounded_without_aborting_accepted_trees([Values] bool interrupt, CancellationToken token)
    {
        string[] links = Enumerable.Range(0, 40).Select(index => $"enrtree://{Base32(TestItem.PrivateKeyB.CompressedPublicKey.Bytes)}@tree{index}.test").ToArray();
        string branch = "enrtree-branch:" + string.Join(',', links.Select(Label));
        Dictionary<string, string[]> parent = new()
        {
            [""] = [Root(Label(Leaf)).Replace(EmptyLink, Label(branch))],
            [Label(Leaf)] = [Leaf],
            [Label(branch)] = [branch]
        };
        foreach (string link in links) parent[Label(link)] = [link];
        List<string> domains = [];
        ManualTimeProvider deadline = new();
        TaskCompletionSource interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool paused = false;
        EnrTreeCrawler crawler = new(NullLogger.Instance, domain =>
            {
                domains.Add(domain);
                if (domain == "parent.test") return new EnrDiscoveryTests.DelegateClient(async (query, cancellation) =>
                {
                    if (interrupt && !paused && query == Label(links[31]))
                    {
                        paused = true;
                        interrupted.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellation);
                    }
                    return parent.GetValueOrDefault(query, []);
                });
                string leaf = "enr:" + domain;
                return new CountingClient(new Dictionary<string, string[]>
                {
                    [""] = [SignedRoot(Label(leaf), EmptyLink, TestItem.PrivateKeyB)],
                    [Label(leaf)] = [leaf]
                });
            }, timeProvider: deadline);
        EnrTreeCrawler.CrawlState state = crawler.CreateCrawl("parent.test")!;
        Task<List<string>> first = crawler.SearchTree(state, token).ToListAsync(token).AsTask();
        if (interrupt)
        {
            await interrupted.Task.WaitAsync(token);
            Assert.That(domains, Has.Count.EqualTo(32));
            deadline.AdvanceAndFireTimer(TimeSpan.FromMinutes(5));
        }
        List<string> records = await first;
        Assert.That(state.Paused, Is.EqualTo(interrupt));
        if (interrupt) records.AddRange(await crawler.SearchTree(state, token).ToListAsync(token));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(domains, Has.Count.EqualTo(32));
            Assert.That(records, Is.EqualTo(new[] { Leaf }.Concat(Enumerable.Range(0, 31).Select(index => $"enr:tree{index}.test"))));
            Assert.That(state.Paused, Is.False);
        }
    }

    [Test]
    public async Task Healthy_link_is_discovered_when_a_sibling_root_lookup_fails([Values] bool resolverFailure)
    {
        string key = Base32(TestItem.PrivateKeyB.CompressedPublicKey.Bytes);
        string missing = $"enrtree://{key}@missing.test";
        string healthy = $"enrtree://{key}@healthy.test";
        string links = $"enrtree-branch:{Label(missing)},{Label(healthy)}";
        CountingClient parent = new(new Dictionary<string, string[]>
        {
            [""] = [SignedRoot(Label(Leaf), Label(links), TestItem.PrivateKeyA)],
            [Label(Leaf)] = [Leaf],
            [Label(links)] = [links],
            [Label(missing)] = [missing],
            [Label(healthy)] = [healthy]
        });
        const string childLeaf = "enr:healthy";
        CountingClient child = new(new Dictionary<string, string[]>
        {
            [""] = [SignedRoot(Label(childLeaf), EmptyLink, TestItem.PrivateKeyB)],
            [Label(childLeaf)] = [childLeaf]
        });
        int rootLookups = 0;
        RecordingClient missingRoot = new(query =>
        {
            Assert.That(query, Is.Empty);
            rootLookups++;
            return resolverFailure ? throw new DnsResponseException("resolver unavailable") : [];
        });
        EnrTreeCrawler crawler = new(NullLogger.Instance, domain => domain switch
        {
            "parent.test" => parent,
            "healthy.test" => child,
            _ => missingRoot
        });
        EnrTreeCrawler.CrawlState state = crawler.CreateCrawl($"enrtree://{Base32(TestItem.PrivateKeyA.CompressedPublicKey.Bytes)}@parent.test")!;
        List<string> records = [];
        int attempts = resolverFailure ? 3 : 1;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            records.AddRange(await crawler.SearchTree(state, CancellationToken.None).ToListAsync());
            Assert.That(state.Paused, Is.EqualTo(attempt < attempts));
            if (state.Paused) Assert.That(child.Queries, Is.Empty);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootLookups, Is.EqualTo(attempts));
            Assert.That(records, Does.Contain(childLeaf));
        }
    }

    [Test]
    public async Task Linked_tree_uses_its_own_domain_key_and_root([Values] bool wrongSigner, [Values] bool cycle)
    {
        string link = $"enrtree://{Base32(TestItem.PrivateKeyB.CompressedPublicKey.Bytes)}@child.test";
        string parentRoot = SignedRoot(Label(Leaf), Label(link), TestItem.PrivateKeyA);
        string childLeaf = "enr:child";
        string backLink = $"enrtree://{Base32(TestItem.PrivateKeyA.CompressedPublicKey.Bytes)}@parent.test";
        CountingClient parent = new(new Dictionary<string, string[]>
        {
            [""] = [parentRoot],
            [Label(Leaf)] = [Leaf],
            [Label(link)] = [link]
        });
        CountingClient child = new(new Dictionary<string, string[]>
        {
            [""] = [SignedRoot(Label(childLeaf), cycle ? Label(backLink) : EmptyLink, wrongSigner ? TestItem.PrivateKeyA : TestItem.PrivateKeyB)],
            [Label(childLeaf)] = [childLeaf],
            [Label(backLink)] = [backLink]
        });
        List<string> domains = [];
        EnrTreeCrawler crawler = new(NullLogger.Instance, domain =>
            {
                domains.Add(domain);
                return domain == "parent.test" ? parent : child;
            });
        List<string> records = await crawler.SearchTree($"enrtree://{Base32(TestItem.PrivateKeyA.CompressedPublicKey.Bytes)}@parent.test").ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(domains.Distinct(), Is.EqualTo(new[] { "parent.test", "child.test" }));
            Assert.That(parent.Queries.Count(q => q.Length == 0), Is.EqualTo(1));
            Assert.That(child.Queries, Does.Contain(""));
            Assert.That(records, Is.EquivalentTo(wrongSigner ? new[] { Leaf } : new[] { Leaf, childLeaf }));
        }
    }

    [Test]
    public async Task Previously_seen_newer_root_prevents_rollback()
    {
        Dictionary<string, string[]> entries = new() { [""] = [Root(Label(Leaf)).Replace("seq=1", "seq=2")], [Label(Leaf)] = [Leaf] };
        CountingClient client = new(entries);
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => client);
        Assert.That(await crawler.SearchTree("example.test").ToListAsync(), Has.Count.EqualTo(1));
        entries[""] = [Root(Label(Leaf))];
        Assert.That(await crawler.SearchTree("example.test").ToListAsync(), Is.Empty);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Query_deadline_completes_even_if_client_ignores_cancellation(CancellationToken token)
    {
        ManualTimeProvider time = new();
        StalledClient client = new();
        EnrTreeCrawler crawler = new(NullLogger.Instance, _ => client, time, TimeSpan.FromMilliseconds(20));
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<List<string>> crawl = crawler.SearchTree("example.test", cancellation.Token).ToListAsync(cancellation.Token).AsTask();
        try
        {
            await client.Entered.Task.WaitAsync(token);
            Assert.That(time.DueTimes, Is.EqualTo(new[] { TimeSpan.FromMilliseconds(20) }));
            time.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(19));
            Assert.That(crawl.IsCompleted, Is.False);
            time.AdvanceAndFireDueTimers(TimeSpan.FromMilliseconds(1));
            Assert.That(await crawl.WaitAsync(token), Is.Empty);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Test]
    public async Task Equivalent_hash_labels_are_visited_once()
    {
        string label = Label(Leaf);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        string alias = label[..^1] + alphabet[alphabet.IndexOf(label[^1]) + 1];
        string branch = $"enrtree-branch:{label},{alias}";
        CountingClient client = new(new Dictionary<string, string[]>
        {
            [""] = [Root(Label(branch))],
            [Label(branch)] = [branch],
            [label] = [Leaf],
            [alias] = [Leaf]
        });
        List<string> records = await new EnrTreeCrawler(NullLogger.Instance, _ => client).SearchTree("example.test").ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.EqualTo(new[] { Leaf }));
            Assert.That(client.Queries.Count(q => q == label || q == alias), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Crawl_has_bounded_queries_and_records([Values] bool validLeaves)
    {
        CountingClient client = new(BuildLargeTree(validLeaves));
        List<string> records = await new EnrTreeCrawler(NullLogger.Instance, _ => client).SearchTree("example.test").ToListAsync();
        if (validLeaves) Assert.That(records, Has.Count.EqualTo(4096));
        else Assert.That(client.Queries.Count, Is.EqualTo(16384));
    }

    internal static Dictionary<string, string[]> BuildLargeTree(bool validLeaves)
    {
        Dictionary<string, string[]> entries = [];
        List<string> labels = [];
        for (int i = 0; i < (validLeaves ? 5000 : 20000); i++)
        {
            string record = validLeaves ? $"enr:record-{i}" : $"unsupported-{i}";
            string label = Label(record);
            labels.Add(label);
            entries[label] = [record];
        }
        while (labels.Count > 1)
        {
            List<string> parents = [];
            foreach (string[] children in labels.Chunk(100))
            {
                string branch = "enrtree-branch:" + string.Join(',', children);
                string label = Label(branch);
                entries[label] = [branch];
                parents.Add(label);
            }
            labels = parents;
        }
        entries[""] = [Root(labels[0])];
        return entries;
    }

    [Test]
    public async Task Oversized_branch_is_dropped_before_its_children_are_queued()
    {
        string branch = "enrtree-branch:" + string.Join(',', Enumerable.Repeat(Label(Leaf), 200));
        CountingClient client = new(new Dictionary<string, string[]>
        {
            [""] = [Root(Label(branch))],
            [Label(branch)] = [branch],
            [Label(Leaf)] = [Leaf]
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await new EnrTreeCrawler(NullLogger.Instance, _ => client).SearchTree("example.test").ToListAsync(), Is.Empty);
            Assert.That(client.Queries, Does.Not.Contain(Label(Leaf)));
        }
    }

    [Test]
    public async Task Deep_tree_stops_before_unbounded_traversal()
    {
        Dictionary<string, string[]> entries = new() { [Label(Leaf)] = [Leaf] };
        string child = Label(Leaf);
        for (int i = 0; i < 128; i++)
        {
            string branch = $"enrtree-branch:{child}";
            child = Label(branch);
            entries[child] = [branch];
        }
        entries[""] = [Root(child)];
        CountingClient client = new(entries);
        List<string> records = await new EnrTreeCrawler(NullLogger.Instance, _ => client).SearchTree("example.test").ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.Empty);
            Assert.That(client.Queries.Count, Is.LessThanOrEqualTo(66));
        }
    }

    internal static string Root(string hash) => $"enrtree-root:v1 e={hash} l={EmptyLink} seq=1 sig={new string('A', 87)}";

    internal static string Label(string entry) => Base32(ValueKeccak.Compute(Encoding.UTF8.GetBytes(entry)).Bytes[..16]);

    internal static string SignedRoot(string hash, string link, PrivateKey privateKey, ulong sequence = 1)
    {
        string content = $"enrtree-root:v1 e={hash} l={link} seq={sequence}";
        ValueHash256 digest = ValueKeccak.Compute(content);
        Signature signature = new Ecdsa().Sign(privateKey, in digest);
        return content + " sig=" + Base64Url.EncodeToString([.. signature.Bytes, (byte)signature.RecoveryId]);
    }

    internal static string Base32(ReadOnlySpan<byte> hash)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        StringBuilder result = new();
        int buffer = 0;
        int bits = 0;
        foreach (byte value in hash)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits != 0) result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    private sealed class StalledClient : IDnsClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return new TaskCompletionSource<IEnumerable<string>>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
    }

    private sealed class CountingClient(Dictionary<string, string[]> entries) : IDnsClient
    {
        public List<string> Queries { get; } = [];
        public Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult<IEnumerable<string>>(entries.GetValueOrDefault(query, []));
        }
    }

    [Test]
    public async Task Prefix_and_case_aliases_fetch_one_record([Values] bool aliases)
    {
        const string leaf = "enr:one-record";
        string label = Label(leaf);
        string branch = "enrtree-branch:" + (aliases ? $"{label},{label[..20]},{label.ToLowerInvariant()}" : label);
        RecordingClient client = new(query => query switch
        {
            "" => [Root(Label(branch))],
            _ when query == Label(branch) => [branch],
            _ when query.StartsWith(label[..20], StringComparison.OrdinalIgnoreCase) => [leaf],
            _ => []
        });
        List<string> records = await new EnrTreeCrawler(NullLogger.Instance, _ => client).SearchTree("example.test").ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.EqualTo(new[] { leaf }));
            Assert.That(client.Queries.Count(query => query.StartsWith(label[..20], StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
        }
    }

    [TestCase("missing", false)]
    [TestCase("malformed", false)]
    [TestCase("signed", true)]
    public async Task Only_signed_links_are_followed_without_remote_error_logs(string keyKind, bool follow)
    {
        const string parentLeaf = "enr:parent";
        const string childLeaf = "enr:child";
        string key = keyKind switch
        {
            "missing" => "",
            "malformed" => "INVALID@",
            _ => Base32(TestItem.PrivateKeyB.CompressedPublicKey.Bytes) + "@"
        };
        string link = $"enrtree://{key}child.test";
        RecordingClient parent = new(query => query switch
        {
            "" => [SignedRoot(Label(parentLeaf), Label(link), TestItem.PrivateKeyA)],
            _ when query == Label(parentLeaf) => [parentLeaf],
            _ when query == Label(link) => [link],
            _ => []
        });
        RecordingClient child = new(query => query switch
        {
            "" => [SignedRoot(Label(childLeaf), Label("enrtree-branch:"), TestItem.PrivateKeyB)],
            _ when query == Label(childLeaf) => [childLeaf],
            _ => []
        });
        TestLogger errors = new() { IsWarn = false, IsDebug = false, IsTrace = false, IsInfo = false };
        EnrTreeCrawler crawler = new(new ILogger(errors), domain => domain == "parent.test" ? parent : child);
        List<string> records = await crawler.SearchTree("parent.test").ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(records, Is.EqualTo(follow ? new[] { parentLeaf, childLeaf } : [parentLeaf]));
            Assert.That(child.Queries.Count, follow ? Is.GreaterThan(0) : Is.Zero);
            Assert.That(errors.LogList, Is.Empty);
        }
    }

    private sealed class RecordingClient(Func<string, IEnumerable<string>> answer) : IDnsClient
    {
        public List<string> Queries { get; } = [];
        public Task<IEnumerable<string>> Lookup(string query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(answer(query));
        }
    }
}
