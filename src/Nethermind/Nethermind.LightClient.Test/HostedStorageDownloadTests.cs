// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class HostedStorageDownloadTests
{
    [Test]
    public async Task Complete_storage_is_downloaded_across_authenticated_pages([Values(1, 2, 3)] int pageSize)
    {
        (PatriciaTree tree, PathWithStorageSlot[] slots) = CreateStorage();
        Account account = new(1, 2, tree.RootHash, Keccak.OfAnEmptyString);
        int requests = 0;
        IReadOnlyDictionary<ValueHash256, UInt256> result = await HostedStorageDownload.DownloadAsync(tree.RootHash,
            (start, _) =>
            {
                requests++;
                using SlotsAndProofs response = CreatePage(tree, slots, start, pageSize);
                return Task.FromResult(HostedStorageDownload.VerifyPage(new(ValueKeccak.Zero, account), start, response, NullLogManager.Instance));
            }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Is.EqualTo((slots.Length + pageSize - 1) / pageSize));
            Assert.That(result.Count, Is.EqualTo(slots.Length));
            foreach (PathWithStorageSlot slot in slots)
                Assert.That(result[slot.Path], Is.EqualTo(ExecutionProofVerifier.DecodeStorageRlp(slot.SlotRlpValue)));
        }
    }

    [Test]
    public async Task Empty_storage_needs_no_network()
    {
        IReadOnlyDictionary<ValueHash256, UInt256> result = await HostedStorageDownload.DownloadAsync(Keccak.EmptyTreeHash,
            (_, _) => throw new AssertionException("An empty trie needs no range request."), CancellationToken.None);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Incomplete_download_cannot_publish_an_omitted_tail()
    {
        (PatriciaTree tree, PathWithStorageSlot[] slots) = CreateStorage();
        HostedStorageDownload.Page page = new([new(slots[0].Path, ExecutionProofVerifier.DecodeStorageRlp(slots[0].SlotRlpValue))], false);
        await Assert.ThatAsync(() => HostedStorageDownload.DownloadAsync(tree.RootHash, (_, _) => Task.FromResult(page), CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Continuations_must_make_progress([Values] bool duplicate)
    {
        ValueHash256 path = ValueKeccak.Compute([1]);
        HostedStorageDownload.Page page = new(duplicate ? [new(path, 1)] : [], true);
        await Assert.ThatAsync(() => HostedStorageDownload.DownloadAsync(Hash256.Zero, (_, _) => Task.FromResult(page), CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task Download_observes_cancellation()
    {
        using CancellationTokenSource stop = new();
        stop.Cancel();
        await Assert.ThatAsync(() => HostedStorageDownload.DownloadAsync(Hash256.Zero,
            (_, _) => throw new AssertionException("Cancelled downloads must not request peers."), stop.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Maximum_path_cannot_wrap_a_continuation()
    {
        HostedStorageDownload.Page page = new([new(ValueKeccak.MaxValue, 1)], true);
        await Assert.ThatAsync(() => HostedStorageDownload.DownloadAsync(Hash256.Zero, (_, _) => Task.FromResult(page), CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Tampered_or_gapped_storage_is_rejected([Values] bool gap)
    {
        (PatriciaTree tree, PathWithStorageSlot[] slots) = CreateStorage();
        PathWithStorageSlot[] bad = gap ? [slots[0], slots[2]] :
            [new(slots[0].Path, Rlp.Encode((UInt256)999).Bytes), slots[1], slots[2]];
        using SlotsAndProofs response = CreatePage(tree, bad, ValueKeccak.Zero, 3);
        Account account = new(1, 2, tree.RootHash, Keccak.OfAnEmptyString);
        Assert.That(() => HostedStorageDownload.VerifyPage(new(ValueKeccak.Zero, account), ValueKeccak.Zero, response, NullLogManager.Instance),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Empty_unavailable_response_is_not_a_complete_storage_snapshot()
    {
        Account account = new(1, 2, Hash256.Zero, Keccak.OfAnEmptyString);
        using SlotsAndProofs response = new();
        Assert.That(() => HostedStorageDownload.VerifyPage(new(ValueKeccak.Zero, account), ValueKeccak.Zero, response, NullLogManager.Instance),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Proof_only_tail_authenticates_absence()
    {
        (PatriciaTree tree, PathWithStorageSlot[] slots) = CreateStorage();
        ValueHash256 start = slots[^1].Path.IncrementPath();
        using SlotsAndProofs response = CreatePage(tree, [], start, 1);
        Account account = new(1, 2, tree.RootHash, Keccak.OfAnEmptyString);
        HostedStorageDownload.Page page = HostedStorageDownload.VerifyPage(new(ValueKeccak.Zero, account), start, response, NullLogManager.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Slots, Is.Empty);
            Assert.That(page.More, Is.False);
        }
    }

    [Test]
    public void Complete_range_can_be_proven_without_boundary_nodes()
    {
        (PatriciaTree tree, PathWithStorageSlot[] slots) = CreateStorage();
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            { new ArrayPoolList<PathWithStorageSlot>(slots) },
        };
        Account account = new(1, 2, tree.RootHash, Keccak.OfAnEmptyString);
        HostedStorageDownload.Page page = HostedStorageDownload.VerifyPage(new(ValueKeccak.Zero, account), ValueKeccak.Zero, response, NullLogManager.Instance);
        Assert.That(page.More, Is.False);
    }

    [Test]
    public void Invalid_storage_encodings_are_rejected_even_with_matching_roots(
        [Values("80", "00", "8101", "c0")] string encoded)
    {
        byte[] value = Convert.FromHexString(encoded);
        ValueHash256 path = ValueKeccak.Compute([1]);
        PatriciaTree tree = new();
        tree.Set(path.Bytes, value);
        tree.UpdateRootHash();
        Account account = new(1, 2, tree.RootHash, Keccak.OfAnEmptyString);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            { new ArrayPoolList<PathWithStorageSlot>(1) { new(path, value) } },
        };
        Assert.That(() => HostedStorageDownload.VerifyPage(new(ValueKeccak.Zero, account), ValueKeccak.Zero, response, NullLogManager.Instance), Throws.Exception);
    }

    private static (PatriciaTree Tree, PathWithStorageSlot[] Slots) CreateStorage()
    {
        PatriciaTree tree = new();
        PathWithStorageSlot[] slots = Enumerable.Range(1, 3)
            .Select(i => new PathWithStorageSlot(ValueKeccak.Compute(((UInt256)(uint)i).ToBigEndian()), Rlp.Encode((UInt256)(uint)(i * 11)).Bytes))
            .OrderBy(slot => slot.Path).ToArray();
        foreach (PathWithStorageSlot slot in slots) tree.Set(slot.Path.Bytes, slot.SlotRlpValue);
        tree.UpdateRootHash();
        return (tree, slots);
    }

    private static SlotsAndProofs CreatePage(PatriciaTree tree, PathWithStorageSlot[] slots, ValueHash256 start, int pageSize)
    {
        PathWithStorageSlot[] page = slots.Where(slot => slot.Path >= start).Take(pageSize).ToArray();
        ValueHash256 end = page.Length == 0 ? ValueKeccak.MaxValue : page[^1].Path;
        ProofCollector first = new(start.Bytes.ToArray());
        ProofCollector last = new(end.Bytes.ToArray());
        tree.Accept(first, tree.RootHash);
        tree.Accept(last, tree.RootHash);
        byte[][] proofs = first.BuildResult().Concat(last.BuildResult()).DistinctBy(Keccak.Compute).ToArray();
        return new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            { new ArrayPoolList<PathWithStorageSlot>(page) },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proofs)),
        };
    }
}
