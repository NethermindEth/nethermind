// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

/// <summary>
/// Tracks tests that keep returning empty results, i.e. likely no longer exercising anything useful.
/// </summary>
internal sealed class EmptyTestsTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastNonEmpty = new();

    public void OnTestExecuted(TestContext test, JsonNode request, JsonNode response)
    {
        if (request.MethodOrUnknown != "eth_getLogs")
            return;

        DateTimeOffset now = _time.GetUtcNow();
        string id = test.Definition.Id;

        // start the clock on first sighting, then refresh only while results keep coming back
        _lastNonEmpty.GetOrAdd(id, now.AddSeconds(-1));
        if (response["result"] is JsonArray { Count: > 0 })
            _lastNonEmpty[id] = now;
    }

    /// <summary>Ids of tests whose last non-empty response was at or before <paramref name="since"/>, sorted.</summary>
    public IReadOnlyList<string> GetTestIdsEmptySince(DateTime since) =>
        [.. _lastNonEmpty.Where(kvp => kvp.Value.UtcDateTime <= since).Select(static kvp => kvp.Key).Order()];
}
