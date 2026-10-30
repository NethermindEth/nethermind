// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class BeaconBlobSourceTests
{
    [TestCase("{}")]
    [TestCase("{\"data\":{}}")]
    [TestCase("{\"data\":{\"genesis_time\":null}}")]
    [TestCase("{\"data\":{\"genesis_time\":\"invalid\"}}")]
    [TestCase("invalid JSON")]
    public async Task GetSlotBlobs_MalformedClock_IsRetryable(string genesis)
    {
        using HttpClient http = Client(path => new StringContent(path.EndsWith("genesis") ? genesis : "{\"data\":{\"SECONDS_PER_SLOT\":\"12\"}}"));
        await Assert.ThrowsAsync<L1SourceIncompleteException>(() => new BeaconBlobSource(http).GetSlotBlobs(Block(), CancellationToken.None));
    }

    [TestCase("{}")]
    [TestCase("{\"data\":null}")]
    [TestCase("{\"data\":[\"0x00\"]}")]
    [TestCase("{\"data\":[null]}")]
    public async Task GetSlotBlobs_MalformedBlobData_IsRetryable(string data)
    {
        using HttpClient http = Client(path => new StringContent(path.Contains("genesis") ? "{\"data\":{\"genesis_time\":\"1000\"}}"
            : path.Contains("config/spec") ? "{\"data\":{\"SECONDS_PER_SLOT\":\"12\"}}" : data));
        await Assert.ThrowsAsync<L1SourceIncompleteException>(() => new BeaconBlobSource(http).GetSlotBlobs(Block(), CancellationToken.None));
    }

    [TestCase(true, TestName = "DeclaredOversizedClock")]
    [TestCase(false, TestName = "ChunkedOversizedClock")]
    public async Task GetSlotBlobs_OversizedClock_IsRetryable(bool declaredLength)
    {
        using HttpClient http = Client(_ =>
        {
            if (declaredLength)
            {
                ByteArrayContent content = new([]);
                content.Headers.ContentLength = 64 * 1024 + 1;
                return content;
            }
            return new StreamContent(new NonSeekableStream(System.Text.Encoding.UTF8.GetBytes("{}" + new string(' ', 64 * 1024))));
        });
        L1SourceIncompleteException error = (await Assert.ThrowsAsync<L1SourceIncompleteException>(() =>
            new BeaconBlobSource(http).GetSlotBlobs(Block(), CancellationToken.None)))!;
        Assert.That(error.Message, Does.Contain("exceeds 65536 bytes"), "the size guard rejects the body before parsing it");
    }

    [Test]
    public async Task GetSlotBlobs_InvalidFieldElement_IsRetryable()
    {
        string blob = "0x" + new string('f', 64) + new string('0', 131072 * 2 - 64);
        using HttpClient http = Client(path => new StringContent(path.Contains("genesis") ? "{\"data\":{\"genesis_time\":\"1000\"}}"
            : path.Contains("config/spec") ? "{\"data\":{\"SECONDS_PER_SLOT\":\"12\"}}" : "{\"data\":[\"" + blob + "\"]}"));
        L1SourceIncompleteException error = (await Assert.ThrowsAsync<L1SourceIncompleteException>(() =>
            new BeaconBlobSource(http).GetSlotBlobs(Block(), CancellationToken.None)))!;
        Assert.That(error.Message, Does.Contain("invalid blob field element"));
    }

    private static EezL1Block Block() => new() { Number = 1, Timestamp = 1012, Hash = TestItem.KeccakA };

    private static HttpClient Client(Func<string, HttpContent> content) => new(new Handler(content)) { BaseAddress = new Uri("http://beacon/") };

    private sealed class Handler(Func<string, HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(request.RequestUri!.AbsolutePath) });
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
