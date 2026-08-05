// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History;

/// <summary>One block's contribution to the changeset hash chain: the keccak of its concatenated raw chunk
/// payload bytes, in the order <see cref="PeerFedWindowImporter"/> already streams them for the write path.</summary>
public readonly record struct BlockDigest(ulong Block, ValueHash256 Digest);

/// <summary>A pure verdict — verified, or a mismatch isolated to <c>[MismatchRangeStart, MismatchRangeEnd]</c>.
/// Carries no opinion about what to do next: banning the source and selecting an alternate is
/// <see cref="PeerFedWindowImporter"/>'s policy to apply, not this type's or <see cref="WindowImportVerifier"/>'s.</summary>
public readonly record struct WindowImportVerdict(bool Verified, ulong MismatchRangeStart, ulong MismatchRangeEnd)
{
    public static WindowImportVerdict Ok() => new(true, 0, 0);

    public static WindowImportVerdict Mismatch(ulong start, ulong end) => new(false, start, end);
}

/// <summary>
/// Verifies a batch of already-computed per-block digests against a peer-claimed chain hash, without ever
/// fetching changeset data itself: <see cref="PeerFedWindowImporter"/> computes each block's digest incrementally
/// (one <see cref="Nethermind.Core.Crypto.KeccakHash"/> per block) as it already streams and decodes chunks for
/// the write path, so the network is read exactly once on the happy path — this type only folds those digests and
/// asks <see cref="IChangesetHashSource"/> for the peer's claim. On a mismatch it bisects using the same
/// already-collected digests (no re-fetch), returning the isolated bad sub-range as a verdict; refetching from an
/// alternate source and applying ban/alternate-selection policy both belong to the caller.
/// </summary>
/// <remarks>
/// The hash-chain invariant: <c>Chain(floor - 1) = seed</c> (the trusted commitment carried in from whatever
/// preceded this batch — the prior batch's own final chain value, or a protocol-level fixed value for the very
/// first batch of a pivot-seeded window with nothing earlier to trust). Ascending from there,
/// <c>Chain(b) = keccak(Chain(b - 1) || digest(b))</c>. A single corrupted block's digest propagates into every
/// <c>Chain(b)</c> for every block at or after it, so "does <c>Chain(mid)</c> match the claim" is monotonic in
/// <c>mid</c> — exactly the property a binary search needs.
/// </remarks>
public sealed class WindowImportVerifier
{
    public const int MaxBisectionDepth = 32;

    public static ValueHash256 FoldAscending(IReadOnlyList<BlockDigest> digestsInAscendingOrder, ValueHash256 seed)
    {
        ValueHash256 chain = seed;
        foreach (BlockDigest entry in digestsInAscendingOrder)
        {
            chain = CombineHash(chain, entry.Digest);
        }

        return chain;
    }

    /// <summary>Verifies <paramref name="digestsInAscendingOrder"/> (must be dense and gapless over
    /// <c>[fromBlockInclusive, fromBlockInclusive + digests.Count - 1]</c> — the caller's own block-contiguity
    /// checks already guarantee this) against the peer's claim for the batch's last block.</summary>
    public async Task<WindowImportVerdict> VerifyAsync(
        IReadOnlyList<BlockDigest> digestsInAscendingOrder,
        ValueHash256 seed,
        ulong fromBlockInclusive,
        IChangesetHashSource hashSource,
        CancellationToken cancellationToken)
    {
        if (digestsInAscendingOrder.Count == 0) return WindowImportVerdict.Ok();

        ValueHash256 local = FoldAscending(digestsInAscendingOrder, seed);
        ulong lastBlock = digestsInAscendingOrder[^1].Block;
        ValueHash256 claimed = await hashSource.GetClaimedChainHashAsync(lastBlock, cancellationToken);
        if (local == claimed) return WindowImportVerdict.Ok();

        return await BisectAsync(digestsInAscendingOrder, seed, fromBlockInclusive, hashSource, cancellationToken);
    }

    /// <summary>Binary search for the first (lowest-index) block whose chain value mismatches the claim — the
    /// standard "first true in a false*,true* sequence" search. <c>high</c> starts at the last index, which the
    /// caller already knows mismatches; the loop narrows <c>[low, high]</c> until they meet at the exact culprit,
    /// or until <see cref="MaxBisectionDepth"/> is spent, in which case <c>[low, high]</c> is reported as the
    /// still-uncertain residual range rather than a single block.</summary>
    private async Task<WindowImportVerdict> BisectAsync(
        IReadOnlyList<BlockDigest> digestsInAscendingOrder,
        ValueHash256 seed,
        ulong fromBlockInclusive,
        IChangesetHashSource hashSource,
        CancellationToken cancellationToken)
    {
        int low = 0;
        int high = digestsInAscendingOrder.Count - 1;

        for (int depth = 0; depth < MaxBisectionDepth && low < high; depth++)
        {
            int mid = low + (high - low) / 2;
            ValueHash256 localAtMid = FoldAscending(Slice(digestsInAscendingOrder, 0, mid), seed);
            ulong midBlock = digestsInAscendingOrder[mid].Block;
            ValueHash256 claimedAtMid = await hashSource.GetClaimedChainHashAsync(midBlock, cancellationToken);

            if (localAtMid != claimedAtMid) high = mid;
            else low = mid + 1;
        }

        return WindowImportVerdict.Mismatch(digestsInAscendingOrder[low].Block, digestsInAscendingOrder[high].Block);
    }

    private static List<BlockDigest> Slice(IReadOnlyList<BlockDigest> source, int fromInclusive, int toInclusive)
    {
        List<BlockDigest> slice = new(toInclusive - fromInclusive + 1);
        for (int i = fromInclusive; i <= toInclusive; i++)
        {
            slice.Add(source[i]);
        }

        return slice;
    }

    private static ValueHash256 CombineHash(in ValueHash256 chain, in ValueHash256 digest)
    {
        Span<byte> buffer = stackalloc byte[64];
        chain.Bytes.CopyTo(buffer);
        digest.Bytes.CopyTo(buffer[32..]);
        return ValueKeccak.Compute(buffer);
    }
}
