// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Matching;
using Nethermind.Runner.JsonRpc;
using NUnit.Framework;

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class LocalPortMatcherPolicyTests
{
    private static readonly IReadOnlySet<int> HealthPorts = new HashSet<int> { 8545 };

    [TestCase(true, 8545, "127.0.0.1:8545", true, TestName = "Keeps candidate on an allowed local port")]
    [TestCase(true, 8545, "nethermind.example.com", true, TestName = "Keeps candidate when a proxy rewrites the Host header")]
    [TestCase(true, 8551, "127.0.0.1:8551", false, TestName = "Invalidates candidate on a non-allowed port (issue #11166)")]
    [TestCase(true, 0, "127.0.0.1", false, TestName = "Invalidates candidate on port 0 (no local endpoint, fail-closed)")]
    [TestCase(false, 8551, "127.0.0.1:8551", true, TestName = "Leaves candidate without metadata untouched")]
    public async Task ApplyAsync_DeterminesCandidateValidity(bool hasMetadata, int localPort, string host, bool expectedValid)
    {
        Endpoint[] endpoints = [BuildEndpoint(hasMetadata ? new LocalPortMetadata(HealthPorts) : null)];
        CandidateSet candidates = new(endpoints, [[]], [0]);
        DefaultHttpContext context = new()
        {
            Connection = { LocalPort = localPort },
            Request = { Host = new HostString(host) }
        };

        await new LocalPortMatcherPolicy().ApplyAsync(context, candidates);

        Assert.That(candidates.IsValidCandidate(0), Is.EqualTo(expectedValid));
    }

    [TestCase(true, ExpectedResult = true, TestName = "Applies when an endpoint carries the metadata")]
    [TestCase(false, ExpectedResult = false, TestName = "Does not apply without the metadata")]
    public bool AppliesToEndpoints_DependsOnMetadataPresence(bool withMetadata) =>
        new LocalPortMatcherPolicy().AppliesToEndpoints([BuildEndpoint(withMetadata ? new LocalPortMetadata(HealthPorts) : null)]);

    private static Endpoint BuildEndpoint(LocalPortMetadata? metadata) =>
        new(requestDelegate: null,
            metadata: metadata is null ? EndpointMetadataCollection.Empty : new EndpointMetadataCollection(metadata),
            displayName: "test");
}
