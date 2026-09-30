// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class TorrentStorageTests
{
    [Test]
    public void Peer_bitfield_snapshot_tracks_have_and_replaced_bitfield()
    {
        PeerBitfield peer = new(10);
        byte[] snapshot = new byte[2];
        Assert.That(peer.AddTo(snapshot), Is.False);

        peer.SetPiece(0);
        Assert.That(peer.AddTo(snapshot), Is.True);
        Assert.That(snapshot, Is.EqualTo(new byte[] { 0b1000_0000, 0 }));

        peer.ReadBitfield([0b0100_0000]);
        snapshot.AsSpan().Clear();
        Assert.That(peer.AddTo(snapshot), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot, Is.EqualTo(new byte[] { 0b0100_0000, 0 }));
            Assert.That(peer.IsEmpty, Is.False);
        }

        peer.ReadBitfield([0]);
        snapshot.AsSpan().Clear();
        Assert.That(peer.AddTo(snapshot), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot, Is.EqualTo(new byte[] { 0, 0 }));
            Assert.That(peer.IsEmpty, Is.True);
        }

        peer.SetPiece(9);
        snapshot.AsSpan().Clear();
        peer.AddTo(snapshot);
        Assert.That(snapshot, Is.EqualTo(new byte[] { 0, 0b0100_0000 }));

        peer.SetAll();
        snapshot.AsSpan().Clear();
        peer.AddTo(snapshot);
        Assert.That(snapshot, Is.EqualTo(new byte[] { 0xff, 0b1100_0000 }));
    }

    [Test]
    public async Task WritePieceAsync_handles_piece_spanning_multiple_files()
    {
        byte[] pieces = new byte[40];
        BDictionary fileA = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(3)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("a.bin")])));
        BDictionary fileB = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(5)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("b.bin")])));
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("files", new BList([fileA, fileB])),
            new KeyValuePair<string, BValue>("name", Bencode.String("root")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(4)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));
        TorrentMetadata metadata = TorrentMetadata.Decode(Bencode.Encode(root));
        string directory = Path.Combine(Path.GetTempPath(), "nethermind-torrent-tests", Guid.NewGuid().ToString("N"));

        await using (TorrentStorage storage = new(metadata, directory))
        {
            await storage.InitializeAsync(TestContext.CurrentContext.CancellationToken);
            await storage.WritePieceAsync(0, Encoding.ASCII.GetBytes("abcd"), TestContext.CurrentContext.CancellationToken);
            await storage.WritePieceAsync(1, Encoding.ASCII.GetBytes("efgh"), TestContext.CurrentContext.CancellationToken);
            byte[] uploadedBlock = new byte[4];
            Assert.That(await storage.ReadBlockAsync(0, 0, uploadedBlock, TestContext.CurrentContext.CancellationToken), Is.True);
            Assert.That(Encoding.ASCII.GetString(uploadedBlock), Is.EqualTo("abcd"));
        }

        byte[] fileABytes = await File.ReadAllBytesAsync(Path.Combine(directory, "root", "a.bin"), TestContext.CurrentContext.CancellationToken);
        byte[] fileBBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "root", "b.bin"), TestContext.CurrentContext.CancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Encoding.ASCII.GetString(fileABytes), Is.EqualTo("abc"));
            Assert.That(Encoding.ASCII.GetString(fileBBytes), Is.EqualTo("defgh"));
        }
    }

    [Test]
    public async Task VerifyAsync_recovers_only_matching_existing_pieces_without_modifying_files()
    {
        byte[] hashes = [.. SHA1.HashData("abcd"u8), .. SHA1.HashData("efgh"u8)];
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(8)),
            new KeyValuePair<string, BValue>("name", Bencode.String("payload.bin")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(4)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(hashes)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));
        TorrentMetadata metadata = TorrentMetadata.Decode(Bencode.Encode(root));
        string directory = Path.Combine(Path.GetTempPath(), "nethermind-torrent-tests", Guid.NewGuid().ToString("N"));
        string payloadPath = Path.Combine(directory, "payload.bin");

        try
        {
            TorrentVerificationProgress missing = await TorrentDataVerifier.VerifyAsync(metadata, directory, token: TestContext.CurrentContext.CancellationToken);
            Assert.That(missing.VerifiedPieces, Is.Zero);
            Assert.That(missing.VerifiedBitfield, Is.EqualTo(new byte[] { 0 }));
            Assert.That(Directory.Exists(directory), Is.False);

            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(payloadPath, "abcd"u8.ToArray(), TestContext.CurrentContext.CancellationToken);
            TorrentVerificationProgress partial = await TorrentDataVerifier.VerifyAsync(metadata, directory, token: TestContext.CurrentContext.CancellationToken);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(partial.VerifiedPieces, Is.EqualTo(1));
                Assert.That(partial.VerifiedBytes, Is.EqualTo(4));
                Assert.That(partial.VerifiedBitfield, Is.EqualTo(new byte[] { 0b1000_0000 }));
                Assert.That(new FileInfo(payloadPath).Length, Is.EqualTo(4));
            }

            await File.WriteAllBytesAsync(payloadPath, "abcdEFGH"u8.ToArray(), TestContext.CurrentContext.CancellationToken);
            TorrentVerificationProgress corrupt = await TorrentDataVerifier.VerifyAsync(metadata, directory, token: TestContext.CurrentContext.CancellationToken);
            Assert.That(corrupt.VerifiedPieces, Is.EqualTo(1));
            Assert.That(corrupt.VerifiedBitfield, Is.EqualTo(new byte[] { 0b1000_0000 }));

            await File.WriteAllBytesAsync(payloadPath, "abcdefgh"u8.ToArray(), TestContext.CurrentContext.CancellationToken);
            TorrentVerificationProgress complete = await TorrentDataVerifier.VerifyAsync(metadata, directory, token: TestContext.CurrentContext.CancellationToken);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(complete.VerifiedPieces, Is.EqualTo(2));
                Assert.That(complete.VerifiedBytes, Is.EqualTo(8));
                Assert.That(complete.VerifiedBitfield, Is.EqualTo(new byte[] { 0b1100_0000 }));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
