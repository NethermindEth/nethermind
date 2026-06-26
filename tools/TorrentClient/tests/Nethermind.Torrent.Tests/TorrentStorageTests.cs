// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class TorrentStorageTests
{
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
        }

        byte[] fileABytes = await File.ReadAllBytesAsync(Path.Combine(directory, "root", "a.bin"), TestContext.CurrentContext.CancellationToken);
        byte[] fileBBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "root", "b.bin"), TestContext.CurrentContext.CancellationToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Encoding.ASCII.GetString(fileABytes), Is.EqualTo("abc"));
            Assert.That(Encoding.ASCII.GetString(fileBBytes), Is.EqualTo("defgh"));
        }
    }
}
