// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class TorrentMetadataTests
{
    [Test]
    public void Decode_preserves_raw_info_hash()
    {
        byte[] pieceHash = SHA1.HashData(Encoding.ASCII.GetBytes("payload"));
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(7)),
            new KeyValuePair<string, BValue>("name", Bencode.String("payload.bin")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(7)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieceHash)));
        byte[] infoBytes = Bencode.Encode(info);
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        TorrentMetadata metadata = TorrentMetadata.Decode(Bencode.Encode(root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Name, Is.EqualTo("payload.bin"));
            Assert.That(metadata.TotalLength, Is.EqualTo(7));
            Assert.That(metadata.PieceCount, Is.EqualTo(1));
            Assert.That(metadata.InfoHash, Is.EqualTo(SHA1.HashData(infoBytes)));
            Assert.That(metadata.Trackers, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Decode_supports_multifile_layout()
    {
        byte[] pieces = new byte[40];
        BDictionary fileA = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(3)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("a.txt")])));
        BDictionary fileB = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(5)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("dir"), Bencode.String("b.txt")])));
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("files", new BList([fileA, fileB])),
            new KeyValuePair<string, BValue>("name", Bencode.String("root")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(4)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        TorrentMetadata metadata = TorrentMetadata.Decode(Bencode.Encode(root));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.TotalLength, Is.EqualTo(8));
            Assert.That(metadata.Files, Has.Count.EqualTo(2));
            Assert.That(metadata.Files[0].Offset, Is.EqualTo(0));
            Assert.That(metadata.Files[1].Offset, Is.EqualTo(3));
            Assert.That(metadata.GetPieceSize(1), Is.EqualTo(4));
        }
    }

    [Test]
    public void Decode_rejects_negative_file_length()
    {
        byte[] pieces = new byte[40];
        BDictionary fileA = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(-1)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("a.txt")])));
        BDictionary fileB = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(3)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("b.txt")])));
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("files", new BList([fileA, fileB])),
            new KeyValuePair<string, BValue>("name", Bencode.String("root")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        Assert.That(() => TorrentMetadata.Decode(Bencode.Encode(root)), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void Decode_rejects_excessive_piece_length()
    {
        byte[] pieces = new byte[20];
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("name", Bencode.String("payload.bin")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer((long)TorrentMetadata.MaxPieceLength + 1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        Assert.That(() => TorrentMetadata.Decode(Bencode.Encode(root)), Throws.TypeOf<FormatException>());
    }

    [TestCase("CON")]
    [TestCase("NUL.txt")]
    [TestCase("COM1")]
    [TestCase("COM\u00b9.txt")]
    [TestCase("LPT9.iso")]
    [TestCase("LPT\u00b3")]
    public void Decode_rejects_reserved_windows_file_names(string fileName)
    {
        byte[] pieces = new byte[20];
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("name", Bencode.String(fileName)),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        Assert.That(() => TorrentMetadata.Decode(Bencode.Encode(root)), Throws.TypeOf<FormatException>());
    }

    [TestCase("...")]
    [TestCase("payload.")]
    public void Decode_rejects_windows_dot_normalized_file_names(string fileName)
    {
        byte[] pieces = new byte[20];
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("name", Bencode.String(fileName)),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        Assert.That(() => TorrentMetadata.Decode(Bencode.Encode(root)), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void Decode_rejects_duplicate_output_paths_after_sanitization()
    {
        byte[] pieces = new byte[40];
        BDictionary fileA = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("A.txt")])));
        BDictionary fileB = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("a.txt")])));
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("files", new BList([fileA, fileB])),
            new KeyValuePair<string, BValue>("name", Bencode.String("root")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        Assert.That(() => TorrentMetadata.Decode(Bencode.Encode(root)), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void Decode_rejects_file_directory_path_collisions()
    {
        byte[] pieces = new byte[40];
        BDictionary fileA = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("a")])));
        BDictionary fileB = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("path", new BList([Bencode.String("a"), Bencode.String("b.txt")])));
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("files", new BList([fileA, fileB])),
            new KeyValuePair<string, BValue>("name", Bencode.String("root")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(pieces)));
        BDictionary root = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("announce", Bencode.String("http://tracker.example/announce")),
            new KeyValuePair<string, BValue>("info", info));

        Assert.That(() => TorrentMetadata.Decode(Bencode.Encode(root)), Throws.TypeOf<FormatException>());
    }
}
