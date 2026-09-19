// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Pbt.Image;

/// <summary>Advisory provenance supplied by the offline producer, never a trust anchor.</summary>
internal sealed record PbtArtifactIdentity(string ChainId, string GenesisHash, string AnchorHash, ulong AnchorNumber,
    string AnchorMptRoot, string FormatRevision, string ProducerRevision, string SourceKind);

/// <summary>Writes two canonical streams and their deterministic version-one manifest.</summary>
/// <remarks>Inputs are already sorted offline streams. Outputs must be separate, unpublished streams owned by the caller.
/// This helper neither verifies roots nor publishes state. On failure the caller discards partial outputs.</remarks>
internal static class PbtArtifactWriter
{
    public static void Write(Stream snapshot, Stream preimages, Stream manifest, PbtArtifactIdentity identity,
        ValueHash256 pbtRoot, ulong leafCount, IEnumerable<RebuildEntry> leaves,
        IEnumerable<PbtAccountPreimages> accounts, CancellationToken cancellationToken = default)
    {
        using DigestWriter snapshotWriter = new(snapshot);
        using DigestWriter preimageWriter = new(preimages);
        PbtSnapshotCodec.Write(snapshotWriter, pbtRoot, leafCount, leaves, cancellationToken);
        PbtPreimageCodec.Write(preimageWriter, accounts, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using Utf8JsonWriter json = new(manifest);
        json.WriteStartObject();
        json.WriteNumber("version", 1);
        json.WriteString("chainId", identity.ChainId);
        json.WriteString("genesisHash", identity.GenesisHash);
        json.WriteString("anchorHash", identity.AnchorHash);
        json.WriteNumber("anchorNumber", identity.AnchorNumber);
        json.WriteString("anchorMptRoot", identity.AnchorMptRoot);
        json.WriteString("pbtRoot", pbtRoot.ToString());
        json.WriteString("snapshotDigest", snapshotWriter.Digest.ToString());
        json.WriteString("preimageDigest", preimageWriter.Digest.ToString());
        json.WriteString("formatRevision", identity.FormatRevision);
        json.WriteString("producerRevision", identity.ProducerRevision);
        json.WriteString("sourceKind", identity.SourceKind);
        json.WriteEndObject();
        json.Flush();
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
