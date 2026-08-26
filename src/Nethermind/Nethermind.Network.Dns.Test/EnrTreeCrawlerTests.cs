// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Dns.Test;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class EnrTreeCrawlerTests
{
    private const string Signature = "CNoJofW_lNh7QFQkaVGhEX2ifbEZ3UkiBQCVyZCkM_I-72cEh8Bfd21cSS9BP5tyAqWF3jMVov8duUCdSByEQAE";
    private const string EmptyLinkRoot = "FDXN3SN67NA5DKA4J2GOK7BVQI";

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

        Assert.That(records, Is.EqualTo(new[] { LeafText }));
        Assert.That(logger.LogList, Has.None.Contains("does not hash"));
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

        Assert.That(records, Is.Empty);
        Assert.That(logger.LogList, Has.Some.Contains("does not hash to the subdomain"));
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

        Assert.That(client.Queries, Is.SupersetOf(new[] { FirstChildLabel, SecondChildLabel }));
        Assert.That(logger.LogList, Has.None.Contains("does not hash"));
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
        Assert.That(client.Queries, Has.None.EqualTo("AAE6C2QIK4C66JK4Z47XCKUZCI"));
        Assert.That(logger.LogList, Has.Some.Contains("does not hash to the subdomain"));
    }

    private static string RootPointingAt(string enrRoot) =>
        $"enrtree-root:v1 e={enrRoot} l={EmptyLinkRoot} seq=1 sig={Signature}";

    private static async Task<(List<string> Records, TestLogger Logger)> Crawl(FakeDnsClient client)
    {
        TestLogger logger = new();
        EnrTreeCrawler crawler = new(new ILogger(logger));
        List<string> records = [];
        await foreach (string record in crawler.SearchTree(client))
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
}
