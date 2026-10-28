// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Stats.Model;

namespace Nethermind.LightClient.Test;

public class VerifiedHeaderHistoryTests
{
    [Test]
    public async Task Invalid_or_wholly_optimistic_queries_do_not_open_history_storage([Values] bool optimistic)
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "history-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using ExecutionPeerTransport transport = new("sepolia", LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.SepoliaGenesis, Keccak.Zero), 0, directory);
            using CancellationTokenSource stop = new();
            VerifiedHead anchor = new(650, 650, Keccak.Zero, Keccak.Zero);
            VerifiedHead finalized = optimistic ? new(100, 100, Keccak.Zero, Keccak.Zero) : anchor;
            Task<BlockHeader[]> request = transport.GetLogHeadersAsync(anchor, finalized, optimistic ? 139UL : 138UL, 650, stop.Token);
            if (optimistic)
            {
                stop.Cancel();
                Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            }
            else Assert.That(async () => await request, Throws.TypeOf<RpcException>());
            Lazy<VerifiedHeaderHistory> history = (Lazy<VerifiedHeaderHistory>)typeof(ExecutionPeerTransport)
                .GetField("_logHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(history.IsValueCreated, Is.False);
                Assert.That(Directory.Exists(Path.Combine(directory, "el", "sepolia", "log-history")), Is.False);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task Shared_log_verifier_supports_wide_pinned_ranges(
        [Values(100UL, 300UL, 650UL)] ulong finality, [Values(499, 512)] int width)
    {
        Chain chain = new(650);
        IExecutionStateSource source = chain;
        ulong first = 651 - (ulong)width;
        BlockHeader[] result = await source.GetLogHeadersAsync(chain.Head(650), chain.Head(finality), first, 650, CancellationToken.None);
        AssertHeaders(result, chain, first, 650);
        Assert.That(chain.AncestorDepths, Is.All.LessThanOrEqualTo(256));
    }

    [Test]
    public void Shared_log_verifier_keeps_range_and_optimistic_work_bounded(
        [Values(513, 514)] int width)
    {
        Chain chain = new(700);
        IExecutionStateSource source = chain;
        Assert.That(async () => await source.GetLogHeadersAsync(chain.Head(700), chain.Head(300),
            701 - (ulong)width, 700, CancellationToken.None), Throws.TypeOf<RpcException>());
        Assert.That(chain.AncestorDepths, Is.Empty);
    }

    [Test]
    public void Shared_log_verifier_rejects_forged_hashes_headers_and_finality(
        [Values("hash", "rlp", "finality", "length")] string corruption)
    {
        Chain chain = new(700);
        if (corruption is "hash" or "length")
            chain.TransformHashes = hashes => corruption == "length" ? hashes[..^1] : [chain.Headers[100].Hash!, .. hashes[1..]];
        if (corruption == "rlp")
            chain.TransformHeader = header => { BlockHeader changed = header.Clone(); changed.ExtraData = [0xff]; return changed; };
        VerifiedHead finality = chain.Head(300);
        if (corruption == "finality") finality = finality with { BlockHash = Keccak.Zero };
        IExecutionStateSource source = chain;
        Assert.That(async () => await source.GetLogHeadersAsync(chain.Head(700), finality, 201, 699, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Backfill_saves_pages_once_and_reuses_complete_verified_ranges()
    {
        Chain chain = new(700);
        await using VerifiedHeaderHistory history = Create(chain, new MemDb());
        BlockHeader[] first = await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None);
        AssertHeaders(first, chain, 100, 598);
        int requests = chain.PageRequests;
        BlockHeader[] second = await history.GetHeadersAsync(chain.Head(600), 101, 599, CancellationToken.None);
        AssertHeaders(second, chain, 101, 599);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Is.EqualTo(4));
            Assert.That(chain.PageRequests, Is.EqualTo(requests));
            Assert.That(chain.AnchorRequests, Is.EqualTo(1));
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(100));
            Assert.That(history.Progress.LastVerifiedBlock, Is.EqualTo(600));
        }
    }

    [Test]
    public async Task Request_cancellation_leaves_one_shared_worker_and_another_waiter_running(
        [Values(50UL, 101UL)] ulong secondFirst)
    {
        Chain chain = new(700) { PausePage = 2 };
        await using VerifiedHeaderHistory history = Create(chain, new MemDb());
        using CancellationTokenSource firstStop = new();
        Task<BlockHeader[]> first = history.GetHeadersAsync(chain.Head(600), 100, 598, firstStop.Token);
        await chain.PagePaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<BlockHeader[]> second = history.GetHeadersAsync(chain.Head(600), secondFirst, secondFirst + 498, CancellationToken.None);
        firstStop.Cancel();
        Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.WorkerCancellationObserved, Is.False);
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(472));
            Assert.That(history.Progress.IsBackfilling, Is.True);
            Assert.That(second.IsCompleted, Is.False);
        }
        chain.ReleasePage.TrySetResult();
        AssertHeaders(await second.WaitAsync(TimeSpan.FromSeconds(10)), chain, secondFirst, secondFirst + 498);
        Assert.That(chain.PageRequests, Is.EqualTo(secondFirst == 50 ? 5 : 4));
    }

    [Test]
    public async Task Worker_deadline_and_shutdown_join_the_pending_page([Values] bool shutdown)
    {
        Chain chain = new(700) { PausePage = 2 };
        DeadlineClock clock = new();
        VerifiedHeaderHistory history = Create(chain, new MemDb(), clock: clock);
        Task<BlockHeader[]> request = history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None);
        try
        {
            await chain.PagePaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (shutdown) await history.DisposeAsync();
            else clock.Expire();
            if (shutdown)
                Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
            else
                Assert.That(async () => await request.WaitAsync(TimeSpan.FromSeconds(10)), Throws.TypeOf<RpcException>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(chain.WorkerCancellationObserved, Is.True);
                Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(472));
                Assert.That(history.Progress.IsBackfilling, Is.False);
                Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromMinutes(30)));
            }
        }
        finally { await history.DisposeAsync(); }
    }

    [Test]
    public async Task Background_backfill_repairs_concurrent_corruption_after_all_requesters_cancel()
    {
        Chain chain = new(700) { PausePage = 2 };
        MemDb db = new();
        await using VerifiedHeaderHistory history = Create(chain, db);
        using CancellationTokenSource firstStop = new();
        using CancellationTokenSource secondStop = new();
        Task<BlockHeader[]> first = history.GetHeadersAsync(chain.Head(600), 100, 598, firstStop.Token);
        await chain.PagePaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, 550);
        db.Set(key, [0xc0]);
        Task<BlockHeader[]> second = history.GetHeadersAsync(chain.Head(600), 550, 550, secondStop.Token);
        Assert.That(history.Progress.FirstVerifiedBlock, Is.Null, "A corrupt read must revoke cached coverage.");
        Task worker = (Task)typeof(VerifiedHeaderHistory).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
        firstStop.Cancel();
        secondStop.Cancel();
        Assert.That(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(async () => await second, Throws.InstanceOf<OperationCanceledException>());
        chain.ReleasePage.TrySetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(100));
            Assert.That(history.Progress.LastVerifiedBlock, Is.EqualTo(600));
            Assert.That(history.Progress.IsBackfilling, Is.False);
            Assert.That(chain.PageRequests, Is.EqualTo(5));
        }
    }

    [Test]
    public async Task Failed_pages_or_commits_never_publish_coverage_and_later_requests_resume(
        [Values("page", "commit")] string failure)
    {
        Chain chain = new(700);
        if (failure == "page") chain.CorruptPage = 2;
        FailCommitDb db = new() { FailAt = failure == "commit" ? 3 : 0 };
        await using VerifiedHeaderHistory history = Create(chain, db);
        Assert.That(async () => await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None),
            Throws.TypeOf<RpcException>());
        Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(472));
        chain.CorruptPage = 0;
        db.FailAt = 0;
        AssertHeaders(await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None), chain, 100, 598);
    }

    [Test]
    public async Task Advancing_finality_bridges_only_the_new_gap_and_evicts_fixed_slots()
    {
        Chain chain = new(800);
        MemDb db = new();
        await using VerifiedHeaderHistory history = Create(chain, db, capacity: 512);
        await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None);
        int requests = chain.PageRequests;
        AssertHeaders(await history.GetHeadersAsync(chain.Head(700), 200, 698, CancellationToken.None), chain, 200, 698);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.PageRequests - requests, Is.EqualTo(1));
            Assert.That(db.Count, Is.LessThanOrEqualTo(513));
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(189));
            Assert.That(history.Progress.LastVerifiedBlock, Is.EqualTo(700));
        }
        Assert.That(async () => await history.GetHeadersAsync(chain.Head(700), 188, 300, CancellationToken.None),
            Throws.TypeOf<RpcException>());
    }

    [Test]
    public async Task Persisted_headers_reattach_to_current_finality_and_detect_corruption(
        [Values("none", "header", "missing")] string corruption, [Values] bool relativePath)
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "history-tests", Guid.NewGuid().ToString("N"));
        string dbPath = relativePath ? Path.GetRelativePath(Directory.GetCurrentDirectory(), directory) : directory;
        Chain chain = new(700);
        try
        {
            await using (VerifiedHeaderHistory history = Create(chain, VerifiedHeaderHistory.OpenDb(dbPath, LimboLogs.Instance)))
                await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None);
            if (corruption != "none")
            {
                using IDb db = VerifiedHeaderHistory.OpenDb(dbPath, LimboLogs.Instance);
                Span<byte> key = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(key, 200);
                BlockHeader changed = chain.Headers[200].Clone();
                changed.ExtraData = [0xee];
                db.Set(key, corruption == "missing" ? null : Rlp.Encode(changed).Bytes);
            }
            chain.PageRequests = 0;
            chain.AnchorRequests = 0;
            if (corruption == "none") chain.ForbidNetwork = true;
            await using VerifiedHeaderHistory resumed = Create(chain, VerifiedHeaderHistory.OpenDb(dbPath, LimboLogs.Instance));
            Assert.That(resumed.Progress.FirstVerifiedBlock, Is.Null, "Disk metadata cannot restore authenticated coverage.");
            AssertHeaders(await resumed.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None), chain, 100, 598);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(chain.AnchorRequests, Is.Zero);
                Assert.That(chain.PageRequests, Is.EqualTo(corruption == "none" ? 0 : 1));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task Corrupt_warm_records_are_reattached_before_serving()
    {
        Chain chain = new(700);
        MemDb db = new();
        await using VerifiedHeaderHistory history = Create(chain, db);
        await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None);
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, 200);
        db.Set(key, [0xc0]);
        int requests = chain.PageRequests;
        AssertHeaders(await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None), chain, 100, 598);
        Assert.That(chain.PageRequests - requests, Is.EqualTo(1));
    }

    [Test]
    public void Page_authentication_rejects_incomplete_reordered_or_forged_input(
        [Values("short", "empty", "order", "number", "hash", "rlp", "null")] string corruption)
    {
        Chain chain = new(200);
        BlockHeader[] page = chain.Headers[72..200];
        switch (corruption)
        {
            case "short": page = page[..^1]; break;
            case "empty": page = []; break;
            case "order": (page[0], page[1]) = (page[1], page[0]); break;
            case "null": page[0] = null!; break;
            default:
                page[0] = page[0].Clone();
                if (corruption == "number") page[0].Number++;
                if (corruption == "hash") page[0].Hash = Keccak.Zero;
                if (corruption == "rlp") page[0].ExtraData = [0xff];
                break;
        }
        Assert.That(() => VerifiedHeaderHistory.VerifyPage(chain.Headers[200], 128, page), Throws.TypeOf<InvalidDataException>());
    }

    [TestCaseSource(nameof(HeaderPageLimits))]
    public async Task Native_header_pages_keep_each_received_prefix(
        int[] responseLimits)
    {
        Chain chain = new(700);
        List<TrackedHeaders> responses = [];
        List<(ulong Number, int Requested)> requests = [];
        HeaderPeer peer = new((number, requested, _) =>
        {
            Assert.That(responses, Is.All.Matches<TrackedHeaders>(item => item is { Disposed: true }),
                "A response must be released before another page is requested.");
            int count = Math.Min(responseLimits[requests.Count % responseLimits.Length], requested);
            requests.Add((number, requested));
            TrackedHeaders response = new(chain.Headers.Skip(checked((int)number)).Take(count).ToArray());
            responses.Add(response);
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(response);
        });

        BlockHeader[] result = await ExecutionPeerTransport.ReadLogHeaderPageAsync(peer, chain.Headers[600], 128,
            CancellationToken.None);

        AssertHeaders(result, chain, 472, 599);
        ulong[] expectedNumbers;
        int[] expectedRequests;
        if (responseLimits.Length == 1)
        {
            int effectiveLimit = responseLimits[0];
            int count = (128 + effectiveLimit - 1) / effectiveLimit;
            expectedNumbers = Enumerable.Range(0, count).Select(index => 472UL + (ulong)(index * effectiveLimit)).ToArray();
            expectedRequests = Enumerable.Range(0, count).Select(index => 128 - index * effectiveLimit).ToArray();
        }
        else
        {
            expectedNumbers = [472, 489, 520, 521, 538, 569, 570, 587];
            expectedRequests = [128, 111, 80, 79, 62, 31, 30, 13];
        }
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(requests.Count, Is.EqualTo(expectedNumbers.Length));
        Assert.That(requests.Select(request => request.Number), Is.EqualTo(expectedNumbers), "No received prefix may be requested again.");
        Assert.That(requests.Select(request => request.Requested), Is.EqualTo(expectedRequests));
        Assert.That(responses, Is.All.Matches<TrackedHeaders>(item => item is { Disposed: true }));
    }

    private static IEnumerable<TestCaseData> HeaderPageLimits()
    {
        foreach (int responseLimit in new[] { 1, 17, 128 })
            yield return new(new[] { responseLimit });
        yield return new(new[] { 17, 31, 1 });
    }

    [TestCaseSource(nameof(HeaderPageFailures))]
    public async Task Native_header_pages_reject_invalid_segments_and_release_every_owner(string failure, bool afterPrefix)
    {
        Chain chain = new(700);
        BlockHeader cursor = chain.Headers[600].Clone();
        if (failure == "anchor") cursor.ParentHash = Keccak.Zero;
        List<TrackedHeaders> responses = [];
        List<(ulong Number, int Requested)> requests = [];
        using CancellationTokenSource stop = new();
        bool injected = false;
        HeaderPeer peer = new((number, requested, _) =>
        {
            Assert.That(responses, Is.All.Matches<TrackedHeaders>(item => item is { Disposed: true }),
                "A response must be released before another page is requested.");
            requests.Add((number, requested));
            BlockHeader[] headers = chain.Headers.Skip(checked((int)number)).Take(Math.Min(17, requested)).ToArray();
            if (!injected && (!afterPrefix || requests.Count > 1))
            {
                injected = true;
                switch (failure)
                {
                    case "null": return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(null);
                    case "empty": headers = []; break;
                    case "over-count": headers = chain.Headers.Skip(checked((int)number)).Take(requested + 1).ToArray(); break;
                    case "cancel":
                        stop.Cancel();
                        return Task.FromCanceled<IOwnedReadOnlyList<BlockHeader>?>(stop.Token);
                    case "null-header": headers[0] = null!; break;
                    case "number": headers[0] = headers[0].Clone(); headers[0].Number++; break;
                    case "oversize": headers[0] = headers[0].Clone(); headers[0].ExtraData = new byte[VerifiedHeaderHistory.MaxHeaderBytes]; break;
                    case "hash": headers[0] = headers[0].Clone(); headers[0].Hash = Keccak.Zero; break;
                }
            }
            TrackedHeaders response = new(headers);
            responses.Add(response);
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(response);
        });

        Task<BlockHeader[]> request = ExecutionPeerTransport.ReadLogHeaderPageAsync(peer, cursor, 128, stop.Token);
        if (failure == "cancel") Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
        else Assert.That(async () => await request, Throws.TypeOf<InvalidDataException>());

        Assert.That(requests.Count, Is.GreaterThanOrEqualTo(afterPrefix ? 2 : 1));
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(injected, Is.True);
        Assert.That(requests.Count, Is.EqualTo(failure is "hash" or "anchor" ? 8 : afterPrefix ? 2 : 1),
            "Invalid size/shape must stop retention immediately; an intrinsically shaped forged chain must reach final anchored verification.");
        Assert.That(requests[0], Is.EqualTo((472UL, 128)));
        if (afterPrefix) Assert.That(requests[1], Is.EqualTo((489UL, 111)));
        Assert.That(responses, Is.All.Matches<TrackedHeaders>(item => item is { Disposed: true }));
    }

    private static IEnumerable<TestCaseData> HeaderPageFailures()
    {
        foreach (string failure in new[] { "null", "empty", "over-count", "null-header", "number", "oversize", "hash", "cancel" })
            foreach (bool afterPrefix in new[] { false, true })
                yield return new(failure, afterPrefix);
        yield return new("anchor", false);
    }

    [TestCase(0, 600UL)]
    [TestCase(129, 600UL)]
    [TestCase(128, 127UL)]
    public void Native_header_page_bounds_reject_before_requesting(int count, ulong cursorNumber)
    {
        Chain chain = new(700);
        int requests = 0;
        HeaderPeer peer = new((_, _, _) =>
        {
            requests++;
            throw new AssertionException("An invalid page must not reach the peer.");
        });

        Assert.That(async () => await ExecutionPeerTransport.ReadLogHeaderPageAsync(peer, chain.Headers[cursorNumber], count,
            CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        Assert.That(requests, Is.Zero);
    }

    [Test]
    public async Task Prefetch_receives_a_bounded_window_and_authenticates_after_each_upper_commit()
    {
        Chain chain = new(1000);
        MemDb db = new();
        List<ulong> prefetched = [];
        List<ulong> authenticated = [];
        List<int> lead = [];
        bool forbidPrefetch = false;
        VerifiedHeaderHistory.PageRequest Prefetch(ulong number, int count, CancellationToken token)
        {
            if (forbidPrefetch) throw new AssertionException("Warm complete history must not start speculative downloads.");
            token.ThrowIfCancellationRequested();
            prefetched.Add(number);
            BlockHeader[] page = chain.Headers.Skip(checked((int)(number - (ulong)count))).Take(count).ToArray();
            return new(Task.CompletedTask, (cursor, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                lead.Add(prefetched.Count - authenticated.Count);
                authenticated.Add(number);
                Span<byte> key = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(key, (int)(cursor.Number % 1024));
                Assert.That(db.KeyExists(key), Is.True, "The upper canonical cursor must have committed first.");
                VerifiedHeaderHistory.VerifyPage(cursor, count, page);
                return Task.FromResult(page);
            });
        }
        await using VerifiedHeaderHistory history = Create(chain, db, prefetch: Prefetch);
        AssertHeaders(await history.GetHeadersAsync(chain.Head(900), 100, 598, CancellationToken.None), chain, 100, 598);
        Assert.That(lead, Has.Count.EqualTo(6));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.PageRequests, Is.EqualTo(1));
            Assert.That(prefetched, Is.EqualTo(new[] { 772UL, 644UL, 516UL, 388UL, 260UL, 132UL }));
            Assert.That(authenticated, Is.EqualTo(prefetched));
            Assert.That(lead[0], Is.EqualTo(3));
            Assert.That(lead, Is.All.LessThanOrEqualTo(3));
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(100));
        }
        chain.ForbidNetwork = true;
        forbidPrefetch = true;
        AssertHeaders(await history.GetHeadersAsync(chain.Head(900), 100, 598, CancellationToken.None), chain, 100, 598);
    }

    [Test]
    public async Task Prefetch_presence_does_not_authenticate_cached_records([Values] bool corrupt)
    {
        Chain chain = new(700);
        MemDb db = new();
        HeaderDecoder decoder = new(MainnetSpecProvider.Instance);
        List<ulong> prefetched = [];
        VerifiedHeaderHistory.PageRequest Prefetch(ulong number, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            prefetched.Add(number);
            BlockHeader[] page = chain.Headers.Skip(checked((int)(number - (ulong)count))).Take(count).ToArray();
            return new(Task.CompletedTask, (cursor, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                VerifiedHeaderHistory.VerifyPage(cursor, count, page);
                return Task.FromResult(page);
            });
        }
        await using VerifiedHeaderHistory history = Create(chain, db, prefetch: Prefetch);
        for (int number = 344; number < 472; number++)
        {
            byte[] key = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(key, number);
            db.Set(key, corrupt && number == 400 ? [0xc0] : decoder.EncodeAsBytes(chain.Headers[number]));
        }
        AssertHeaders(await history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None), chain, 100, 598);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefetched, Does.Not.Contain(472UL), "Presence only prevents speculation; it never proves a canonical page.");
            Assert.That(chain.PageNumbers, Is.EqualTo(corrupt ? new[] { 600UL, 472UL } : new[] { 600UL, 344UL }));
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(100));
        }
    }

    [Test]
    public async Task Prefetch_fault_and_shutdown_join_every_pending_fetch([Values("commit", "page", "shutdown")] string failure)
    {
        Chain chain = new(700);
        FailCommitDb db = new() { FailAt = failure == "commit" ? 2 : 0 };
        TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource authenticating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task> fetching = [];
        int cancelled = 0;
        async Task Fetch(CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                if (Interlocked.Increment(ref cancelled) == 3) allCancelled.TrySetResult();
                await drain.Task;
            }
        }
        VerifiedHeaderHistory.PageRequest Prefetch(ulong number, int count, CancellationToken token)
        {
            Task fetch = Fetch(token);
            fetching.Add(fetch);
            return new(fetch, Authenticate);
            async Task<BlockHeader[]> Authenticate(BlockHeader cursor, CancellationToken cancellation)
            {
                authenticating.TrySetResult();
                if (failure == "page") throw new InvalidDataException("Injected staged verification failure.");
                await fetch.WaitAsync(cancellation);
                return chain.Headers.Skip(checked((int)(number - (ulong)count))).Take(count).ToArray();
            }
        }
        VerifiedHeaderHistory history = Create(chain, db, prefetch: Prefetch);
        Task<BlockHeader[]> query = history.GetHeadersAsync(chain.Head(600), 100, 598, CancellationToken.None);
        Task? disposing = null;
        try
        {
            if (failure == "shutdown")
            {
                await authenticating.Task;
                disposing = history.DisposeAsync().AsTask();
            }
            await allCancelled.Task;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(fetching, Has.Count.EqualTo(3));
                Assert.That(fetching, Is.All.Matches<Task>(task => task is { IsCompleted: false }));
                Assert.That(disposing?.IsCompleted ?? query.IsCompleted, Is.False, "Cleanup must join delayed unwinding.");
                Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(failure == "commit" ? 600UL : 472UL));
            }
            drain.TrySetResult();
            if (disposing is not null) await disposing;
            if (failure == "shutdown")
                Assert.That(async () => await query, Throws.InstanceOf<OperationCanceledException>());
            else
                Assert.That(async () => await query, Throws.TypeOf<RpcException>());
            Assert.That(fetching, Is.All.Matches<Task>(task => task is { IsCompleted: true }));
        }
        finally
        {
            drain.TrySetResult();
            await history.DisposeAsync();
            await ((Task)query).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await Task.WhenAll(fetching).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Prefetch_bridge_keeps_the_previous_anchor_until_the_final_page_joins()
    {
        Chain chain = new(800);
        BlockHeader[] fork = chain.Headers.Select(header => header.Clone()).ToArray();
        for (int i = 250; i < fork.Length; i++)
        {
            fork[i].ExtraData = [0x61];
            fork[i].ParentHash = fork[i - 1].Hash!;
            fork[i].Hash = fork[i].CalculateHash();
        }
        bool forked = false;
        MemDb db = new();
        BlockHeader[] Page(ulong number, int count) => (forked ? fork : chain.Headers)
            .Skip(checked((int)(number - (ulong)count))).Take(count).ToArray();
        VerifiedHeaderHistory.PageRequest Prefetch(ulong number, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BlockHeader[] page = Page(number, count);
            return new(Task.CompletedTask, (cursor, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                VerifiedHeaderHistory.VerifyPage(cursor, count, page);
                return Task.FromResult(page);
            });
        }
        await using VerifiedHeaderHistory history = new(db, chain.Headers[0].Hash!, MainnetSpecProvider.Instance,
            (head, _) => Task.FromResult((forked ? fork : chain.Headers)[head.Number]),
            (cursor, count, _) => Task.FromResult(Page(cursor.Number, count)),
            LimboLogs.Instance.GetClassLogger<VerifiedHeaderHistory>(), capacity: 1024, prefetchPage: Prefetch);
        AssertHeaders(await history.GetHeadersAsync(chain.Head(300), 100, 298, CancellationToken.None), chain, 100, 298);
        byte[] key = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(key, 300);
        byte[] previous = db.Get(key)!;
        forked = true;
        BlockHeader head = fork[700];
        Assert.That(async () => await history.GetHeadersAsync(new(700, head.Number, head.Hash!, head.StateRoot!),
            100, 598, CancellationToken.None), Throws.TypeOf<RpcException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Progress.LastVerifiedBlock, Is.EqualTo(300));
            Assert.That(history.Progress.FirstVerifiedBlock, Is.EqualTo(100));
            Assert.That(history.Progress.Error, Is.Not.Null);
            Assert.That(db.Get(key), Is.EqualTo(previous), "A conflicting final bridge page must not overwrite the old anchor.");
        }
    }

    private static VerifiedHeaderHistory Create(Chain chain, IDb db, int capacity = 1024, TimeProvider? clock = null,
        Func<ulong, int, CancellationToken, VerifiedHeaderHistory.PageRequest>? prefetch = null) =>
        new(db, chain.Headers[0].Hash!, MainnetSpecProvider.Instance, chain.GetHeaderAsync, chain.FetchPageAsync,
            LimboLogs.Instance.GetClassLogger<VerifiedHeaderHistory>(), clock, capacity, prefetchPage: prefetch);

    private static void AssertHeaders(BlockHeader[] actual, Chain chain, ulong first, ulong last) =>
        Assert.That(actual.Select(header => (header.Number, header.Hash, header.ParentHash, header.StateRoot)),
            Is.EqualTo(chain.Headers.Skip((int)first).Take(checked((int)(last - first + 1)))
                .Select(header => (header.Number, header.Hash, header.ParentHash, header.StateRoot))));

    private sealed class Chain : IExecutionStateSource
    {
        internal readonly BlockHeader[] Headers;
        internal readonly List<ulong> AncestorDepths = [];
        internal readonly TaskCompletionSource PagePaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleasePage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PageRequests;
        internal int AnchorRequests;
        internal int PausePage;
        internal int CorruptPage;
        internal bool WorkerCancellationObserved;
        internal bool ForbidNetwork;
        internal readonly List<ulong> PageNumbers = [];
        internal Func<Hash256[], Hash256[]>? TransformHashes;
        internal Func<BlockHeader, BlockHeader>? TransformHeader;
        private readonly Dictionary<Hash256, BlockHeader> _byHash = [];

        internal Chain(int last)
        {
            Headers = new BlockHeader[last + 1];
            Hash256 parent = Keccak.Zero;
            for (int i = 0; i <= last; i++)
            {
                BlockHeader header = new(parent, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                    (ulong)i, 30_000_000, 1_700_000_000 + (ulong)i * 12, [(byte)(i % 251)])
                {
                    StateRoot = Keccak.Compute(BitConverter.GetBytes(i)),
                    TxRoot = Keccak.EmptyTreeHash,
                    ReceiptsRoot = Keccak.EmptyTreeHash,
                    WithdrawalsRoot = Keccak.EmptyTreeHash,
                };
                header.Hash = parent = header.CalculateHash();
                Headers[i] = header;
                _byHash.Add(parent, header);
            }
        }

        internal VerifiedHead Head(ulong number) => new(number, number, Headers[number].Hash!, Headers[number].StateRoot!);

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref AnchorRequests);
            if (ForbidNetwork) throw new InvalidOperationException("Restart should reuse its reattached cache.");
            return GetHeaderByHashAsync(head.BlockHash, cancellationToken);
        }

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlockHeader header = _byHash[hash];
            return Task.FromResult(TransformHeader?.Invoke(header) ?? header);
        }

        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AncestorDepths.Add(head.Number - firstNumber);
            Assert.That(head.Number - firstNumber, Is.LessThanOrEqualTo(256));
            Hash256[] hashes = Headers.Skip((int)firstNumber).Take(checked((int)(head.Number - firstNumber + 1)))
                .Select(header => header.Hash!).ToArray();
            return Task.FromResult(TransformHashes?.Invoke(hashes) ?? hashes);
        }

        internal async Task<BlockHeader[]> FetchPageAsync(BlockHeader cursor, int count, CancellationToken cancellationToken)
        {
            if (ForbidNetwork) throw new InvalidOperationException("Restart should not download cached ancestry.");
            int request = Interlocked.Increment(ref PageRequests);
            PageNumbers.Add(cursor.Number);
            if (request == PausePage)
            {
                PagePaused.TrySetResult();
                try { await ReleasePage.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { WorkerCancellationObserved = true; throw; }
            }
            BlockHeader[] result = Headers.Skip(checked((int)cursor.Number - count)).Take(count).ToArray();
            if (request == CorruptPage) result[0] = Headers[0];
            return result;
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailCommitDb : MemDb
    {
        internal int FailAt;
        private int _batches;
        public override IWriteBatch StartWriteBatch() => new Batch(base.StartWriteBatch(), ++_batches == FailAt);

        private sealed class Batch(IWriteBatch inner, bool fail) : IWriteBatch
        {
            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => inner.Set(key, value, flags);
            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
            public void Clear() => inner.Clear();
            public void Dispose()
            {
                inner.Dispose();
                if (fail) throw new IOException("Injected page commit failure.");
            }
        }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new Timer();
        }
        internal void Expire() => _callback!(_state);
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

    private sealed class TrackedHeaders(BlockHeader[] headers) : IOwnedReadOnlyList<BlockHeader>
    {
        internal bool Disposed;
        public int Count => headers.Length;
        public BlockHeader this[int index] => headers[index];
        public ReadOnlySpan<BlockHeader> AsSpan() => headers;
        public IEnumerator<BlockHeader> GetEnumerator() => ((IEnumerable<BlockHeader>)headers).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Disposed = true;
    }

    private sealed class HeaderPeer(Func<ulong, int, CancellationToken, Task<IOwnedReadOnlyList<BlockHeader>?>> read) : ISyncPeer
    {
        public Node Node => throw new NotSupportedException();
        public string Name => nameof(HeaderPeer);
        public PublicKey Id => throw new NotSupportedException();
        public Hash256 HeadHash { get; set; } = Keccak.Zero;
        public ulong HeadNumber { get; set; }
        public UInt256? TotalDifficulty { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsPriority { get; set; }
        public byte ProtocolVersion => 69;
        public string ProtocolCode => "eth";
        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong number, int count, int skip, CancellationToken token) => read(number, count, token);
        public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 hash, int count, int skip, CancellationToken token) => throw new NotSupportedException();
        public Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token) => throw new NotSupportedException();
        public Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> hashes, CancellationToken token) => throw new NotSupportedException();
        public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> hashes, CancellationToken token) => throw new NotSupportedException();
        public Task<IByteArrayList> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token) => throw new NotSupportedException();
        public void Disconnect(DisconnectReason reason, string details) => throw new NotSupportedException();
        public void NotifyOfNewBlock(Block block, SendBlockMode mode) => throw new NotSupportedException();
        public void SendNewTransactions(ReadOnlySpan<Transaction> transactions, bool sendFullTx) => throw new NotSupportedException();
        public void RegisterSatelliteProtocol<T>(string protocol, T handler) where T : class => throw new NotSupportedException();
        public bool TryGetSatelliteProtocol<T>(string protocol, out T handler) where T : class => throw new NotSupportedException();
    }
}
