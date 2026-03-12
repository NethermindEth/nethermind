// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using FluentAssertions;
using Nethermind.EraE.Proofs;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Proofs;

public class HistoricalSummariesRpcProviderTests
{
    private const string TwoSummariesJson = """
        {
            "data": {
                "historical_summaries": [
                    {
                        "block_summary_root": "0xaabbccdd00000000000000000000000000000000000000000000000000000000",
                        "state_summary_root": "0x1122334400000000000000000000000000000000000000000000000000000000"
                    },
                    {
                        "block_summary_root": "0xccddee0000000000000000000000000000000000000000000000000000000000",
                        "state_summary_root": "0x5566770000000000000000000000000000000000000000000000000000000000"
                    }
                ]
            }
        }
        """;

    [Test]
    public async Task GetHistoricalSummary_WithValidResponse_ReturnsExpectedSummaryAtIndex()
    {
        using HistoricalSummariesRpcProvider sut = Build(TwoSummariesJson);

        HistoricalSummary? result = await sut.GetHistoricalSummary(0);

        result.Should().NotBeNull();
        result!.Value.BlockSummaryRoot.ToByteArray()[0].Should().Be(0xaa);
        result.Value.StateSummaryRoot.ToByteArray()[0].Should().Be(0x11);
    }

    [Test]
    public async Task GetHistoricalSummary_WithIndexOutOfRange_ReturnsNull()
    {
        using HistoricalSummariesRpcProvider sut = Build(TwoSummariesJson);

        HistoricalSummary? result = await sut.GetHistoricalSummary(999);

        result.Should().BeNull();
    }

    [Test]
    public async Task GetHistoricalSummariesAsync_WithSameCallTwice_ReturnsCachedResultWithoutExtraRequest()
    {
        FakeHttpMessageHandler handler = new(TwoSummariesJson);
        using HistoricalSummariesRpcProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        await sut.GetHistoricalSummariesAsync();
        await sut.GetHistoricalSummariesAsync();

        handler.RequestCount.Should().Be(1);
    }

    [Test]
    public async Task GetHistoricalSummariesAsync_WithForceRefresh_BypassesCache()
    {
        FakeHttpMessageHandler handler = new(TwoSummariesJson);
        using HistoricalSummariesRpcProvider sut = new(
            new Uri("http://localhost:5052"),
            new HttpClient(handler));

        await sut.GetHistoricalSummariesAsync();
        await sut.GetHistoricalSummariesAsync(forceRefresh: true);

        handler.RequestCount.Should().Be(2);
    }

    [Test]
    public async Task GetHistoricalSummariesAsync_WhenMissingDataField_ReturnsEmptyArray()
    {
        using HistoricalSummariesRpcProvider sut = Build("""{"other":"x"}""");

        HistoricalSummary[] result = await sut.GetHistoricalSummariesAsync();

        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetHistoricalSummariesAsync_WhenMissingHistoricalSummariesField_ReturnsEmptyArray()
    {
        using HistoricalSummariesRpcProvider sut = Build("""{"data":{}}""");

        HistoricalSummary[] result = await sut.GetHistoricalSummariesAsync();

        result.Should().BeEmpty();
    }

    [Test]
    public async Task GetHistoricalSummariesAsync_WhenEntryMissingBlockSummaryRootField_SkipsEntry()
    {
        using HistoricalSummariesRpcProvider sut = Build("""
            {
                "data": {
                    "historical_summaries": [
                        {
                            "state_summary_root": "0x1122334400000000000000000000000000000000000000000000000000000000"
                        },
                        {
                            "block_summary_root": "0xaabbccdd00000000000000000000000000000000000000000000000000000000",
                            "state_summary_root": "0x1122334400000000000000000000000000000000000000000000000000000000"
                        }
                    ]
                }
            }
            """);

        HistoricalSummary[] result = await sut.GetHistoricalSummariesAsync();

        result.Should().HaveCount(1);
    }

    private static HistoricalSummariesRpcProvider Build(string responseBody) =>
        new(new Uri("http://localhost:5052"), new HttpClient(new FakeHttpMessageHandler(responseBody)));

    private sealed class FakeHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
