// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Nethermind.Runner.JsonRpc;
using NUnit.Framework;

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class LocalPortMatcherPolicyTests
{
    private static readonly IReadOnlySet<int> HealthPorts = new HashSet<int> { 8545 };

    [TestCase(8545, true, TestName = "Keeps candidate when local port is allowed")]
    [TestCase(8551, false, TestName = "Invalidates candidate on a non-allowed port (issue #11166)")]
    [TestCase(0, false, TestName = "Invalidates candidate on port 0 (no local endpoint, fail-closed)")]
    public async Task ApplyAsync_LocalPort_DeterminesCandidateValidity(int localPort, bool expectedValid)
    {
        CandidateSet candidates = BuildCandidates(new LocalPortMetadata(HealthPorts));

        await new LocalPortMatcherPolicy().ApplyAsync(BuildContext(localPort), candidates);

        Assert.That(candidates.IsValidCandidate(0), Is.EqualTo(expectedValid));
    }

    [Test]
    public async Task ApplyAsync_RewrittenHostHeader_KeepsCandidate()
    {
        CandidateSet candidates = BuildCandidates(new LocalPortMetadata(HealthPorts));
        HttpContext context = BuildContext(localPort: 8545);
        context.Request.Host = new HostString("example.com"); // proxied host carries no :8545

        await new LocalPortMatcherPolicy().ApplyAsync(context, candidates);

        Assert.That(candidates.IsValidCandidate(0), Is.True);
    }

    [Test]
    public async Task ApplyAsync_EndpointWithoutMetadata_KeepsCandidate()
    {
        CandidateSet candidates = BuildCandidates(metadata: null);

        await new LocalPortMatcherPolicy().ApplyAsync(BuildContext(localPort: 1234), candidates);

        Assert.That(candidates.IsValidCandidate(0), Is.True);
    }

    [TestCase(true, ExpectedResult = true, TestName = "Applies when an endpoint carries the metadata")]
    [TestCase(false, ExpectedResult = false, TestName = "Does not apply without the metadata")]
    public bool AppliesToEndpoints_Metadata_DeterminesApplicability(bool withMetadata)
    {
        Endpoint endpoint = BuildEndpoint(withMetadata ? new LocalPortMetadata(HealthPorts) : null);
        return new LocalPortMatcherPolicy().AppliesToEndpoints([endpoint]);
    }

    private static HttpContext BuildContext(int localPort)
    {
        DefaultHttpContext context = new();
        context.Connection.LocalPort = localPort;
        return context;
    }

    private static CandidateSet BuildCandidates(LocalPortMetadata? metadata)
    {
        Endpoint[] endpoints = [BuildEndpoint(metadata)];
        RouteValueDictionary[] values = [[]];
        int[] scores = [0];
        return new CandidateSet(endpoints, values, scores);
    }

    private static Endpoint BuildEndpoint(LocalPortMetadata? metadata) =>
        new(requestDelegate: null,
            metadata: metadata is null ? EndpointMetadataCollection.Empty : new EndpointMetadataCollection(metadata),
            displayName: "test");
}
