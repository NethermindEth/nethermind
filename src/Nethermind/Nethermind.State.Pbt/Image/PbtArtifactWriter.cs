// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Image;

/// <summary>Writes the two canonical EIP-8347 streams and reports their digests.</summary>
/// <remarks>Inputs are already sorted offline streams. Outputs must be separate, unpublished streams owned by the caller.
/// This helper neither verifies roots nor publishes state. On failure the caller discards partial outputs.</remarks>
internal static class PbtArtifactWriter
{
    /// <summary>Keccak over each whole stream, as EIP-8347 defines them. They let consumers agree on an
    /// artifact ahead of an expensive download; they are not roots of trust.</summary>
    internal readonly record struct PbtArtifactDigests(ValueHash256 Snapshot, ValueHash256? Preimages);

    /// <summary>Writes the snapshot stream and returns its digest.</summary>
    /// <param name="calculateRoot">Folds the claimed root from the snapshot leaves as they are written.</param>
    public static ValueHash256 WriteSnapshot(Stream snapshot, IEnumerable<RebuildEntry> leaves,
        Func<IEnumerable<RebuildEntry>, ValueHash256> calculateRoot, CancellationToken cancellationToken = default)
    {
        using DigestWriter snapshotWriter = new(snapshot);
        PbtSnapshotCodec.Write(snapshotWriter, leaves, calculateRoot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshotWriter.Digest;
    }

    /// <summary>Writes the preimage stream and returns its digest.</summary>
    /// <remarks>Independent of the snapshot, so the two streams may be written concurrently.</remarks>
    public static ValueHash256 WritePreimages(Stream preimages, IEnumerable<PbtAccountPreimages> accounts,
        CancellationToken cancellationToken = default)
    {
        using DigestWriter preimageWriter = new(preimages);
        PbtPreimageCodec.Write(preimageWriter, accounts, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return preimageWriter.Digest;
    }

    private sealed class DigestWriter(Stream destination) : Stream
    {
        private readonly KeccakHash _hash = KeccakHash.Create();
        public ValueHash256 Digest => new(_hash.Hash);
        public override void Write(ReadOnlySpan<byte> buffer) { destination.Write(buffer); _hash.Update(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => destination.Flush();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Reset(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
