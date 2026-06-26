// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Nethermind.Torrent;

/// <summary>
/// Describes a torrent file and the immutable metadata needed by trackers and peers.
/// </summary>
public sealed class TorrentMetadata
{
    private static readonly string[] ReservedWindowsNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "COM\u00b9",
        "COM\u00b2",
        "COM\u00b3",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
        "LPT\u00b9",
        "LPT\u00b2",
        "LPT\u00b3",
    ];

    private TorrentMetadata(
        string name,
        long totalLength,
        int pieceLength,
        byte[] pieces,
        byte[] infoHash,
        IReadOnlyList<Uri> trackers,
        IReadOnlyList<TorrentFileEntry> files)
    {
        Name = name;
        TotalLength = totalLength;
        PieceLength = pieceLength;
        Pieces = pieces;
        InfoHash = infoHash;
        Trackers = trackers;
        Files = files;
    }

    /// <summary>
    /// Gets the display name from the torrent info dictionary.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the total payload length in bytes.
    /// </summary>
    public long TotalLength { get; }

    /// <summary>
    /// Gets the piece length in bytes, except the final piece may be shorter.
    /// </summary>
    public int PieceLength { get; }

    /// <summary>
    /// Gets concatenated SHA-1 hashes, one 20-byte hash per piece.
    /// </summary>
    public byte[] Pieces { get; }

    /// <summary>
    /// Gets the SHA-1 hash of the raw bencoded info dictionary.
    /// </summary>
    public byte[] InfoHash { get; }

    /// <summary>
    /// Gets tracker announce URLs from announce and announce-list.
    /// </summary>
    public IReadOnlyList<Uri> Trackers { get; }

    /// <summary>
    /// Gets the file layout for the torrent payload.
    /// </summary>
    public IReadOnlyList<TorrentFileEntry> Files { get; }

    /// <summary>
    /// Gets the number of pieces in the torrent.
    /// </summary>
    public int PieceCount => Pieces.Length / Sha1Length;

    internal const int Sha1Length = 20;
    internal const int MaxPieceLength = 32 * 1024 * 1024;

    /// <summary>
    /// Gets the byte length of a piece.
    /// </summary>
    /// <param name="pieceIndex">The zero-based piece index.</param>
    /// <returns>The piece length in bytes; the final piece may be shorter than <see cref="PieceLength"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="pieceIndex"/> is outside the torrent piece range.</exception>
    public int GetPieceSize(int pieceIndex)
    {
        if ((uint)pieceIndex >= (uint)PieceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pieceIndex));
        }

        if (pieceIndex == PieceCount - 1)
        {
            long consumed = (long)pieceIndex * PieceLength;
            return checked((int)(TotalLength - consumed));
        }

        return PieceLength;
    }

    /// <summary>
    /// Gets the expected 20-byte SHA-1 hash for a piece.
    /// </summary>
    /// <param name="pieceIndex">The zero-based piece index.</param>
    /// <returns>The expected SHA-1 hash as a span over the torrent metadata.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="pieceIndex"/> is outside the torrent piece range.</exception>
    public ReadOnlySpan<byte> GetPieceHash(int pieceIndex)
    {
        if ((uint)pieceIndex >= (uint)PieceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pieceIndex));
        }

        return Pieces.AsSpan(pieceIndex * Sha1Length, Sha1Length);
    }

    /// <summary>
    /// Gets the lowercase hexadecimal representation of <see cref="InfoHash"/>.
    /// </summary>
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();

    /// <summary>
    /// Loads and decodes a torrent metadata file from disk.
    /// </summary>
    /// <param name="path">Path to the `.torrent` file.</param>
    /// <returns>The decoded torrent metadata.</returns>
    public static TorrentMetadata Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    internal static TorrentMetadata Decode(ReadOnlySpan<byte> bytes)
    {
        BencodeDocument document = BencodeDocument.Decode(bytes);
        BDictionary root = document.Root.AsDictionary("root");
        if (document.InfoBytes is null)
        {
            throw new FormatException("Torrent is missing an info dictionary.");
        }

        BDictionary info = root["info"].AsDictionary("info");
        string name = CleanPathSegment(info["name"].AsText("info.name"));
        int pieceLength = checked((int)info["piece length"].AsInteger("info.piece length"));
        byte[] pieces = info["pieces"].AsBytes("info.pieces");
        if (pieceLength <= 0)
        {
            throw new FormatException("Torrent piece length must be positive.");
        }

        if (pieceLength > MaxPieceLength)
        {
            throw new FormatException($"Torrent piece length must not exceed {MaxPieceLength} bytes.");
        }

        if (pieces.Length == 0 || pieces.Length % Sha1Length != 0)
        {
            throw new FormatException("Torrent pieces field must be a non-empty multiple of 20 bytes.");
        }

        byte[] infoHash = SHA1.HashData(document.InfoBytes);
        List<Uri> trackers = ReadTrackers(root);
        List<TorrentFileEntry> files = ReadFiles(info, name);
        RejectDuplicatePaths(files);
        long totalLength = 0;
        for (int i = 0; i < files.Count; i++)
        {
            checked
            {
                totalLength += files[i].Length;
            }
        }

        long expectedPieceCount = (totalLength + pieceLength - 1) / pieceLength;
        if (expectedPieceCount != pieces.Length / Sha1Length)
        {
            throw new FormatException("Torrent pieces count does not match payload length.");
        }

        return new TorrentMetadata(name, totalLength, pieceLength, pieces, infoHash, trackers, files);
    }

    private static List<Uri> ReadTrackers(BDictionary root)
    {
        List<Uri> trackers = [];
        if (root.TryGetValue("announce", out BValue? announce) && announce is not null)
        {
            AddTracker(trackers, announce.AsText("announce"));
        }

        if (root.TryGetValue("announce-list", out BValue? announceListValue) && announceListValue is not null)
        {
            BList announceList = announceListValue.AsList("announce-list");
            for (int i = 0; i < announceList.Values.Count; i++)
            {
                BValue tierValue = announceList.Values[i];
                if (tierValue is BList tier)
                {
                    for (int j = 0; j < tier.Values.Count; j++)
                    {
                        AddTracker(trackers, tier.Values[j].AsText("announce-list tracker"));
                    }
                }
                else
                {
                    AddTracker(trackers, tierValue.AsText("announce-list tracker"));
                }
            }
        }

        return trackers;
    }

    private static void AddTracker(List<Uri> trackers, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        for (int i = 0; i < trackers.Count; i++)
        {
            if (trackers[i] == uri)
            {
                return;
            }
        }

        trackers.Add(uri);
    }

    private static List<TorrentFileEntry> ReadFiles(BDictionary info, string name)
    {
        List<TorrentFileEntry> files = [];
        if (info.TryGetValue("length", out BValue? lengthValue) && lengthValue is not null)
        {
            long length = lengthValue.AsInteger("info.length");
            if (length < 0)
            {
                throw new FormatException("Torrent file length must be non-negative.");
            }

            files.Add(new TorrentFileEntry(name, length, 0));
            return files;
        }

        BList fileList = info["files"].AsList("info.files");
        long offset = 0;
        for (int i = 0; i < fileList.Values.Count; i++)
        {
            BDictionary file = fileList.Values[i].AsDictionary("info.files[]");
            long length = file["length"].AsInteger("file.length");
            if (length < 0)
            {
                throw new FormatException("Torrent file length must be non-negative.");
            }

            BList pathParts = file["path"].AsList("file.path");
            string relativePath = JoinPath(name, pathParts);
            files.Add(new TorrentFileEntry(relativePath, length, offset));
            checked
            {
                offset += length;
            }
        }

        return files;
    }

    private static void RejectDuplicatePaths(List<TorrentFileEntry> files)
    {
        List<string> paths = [];
        for (int i = 0; i < files.Count; i++)
        {
            string normalized = Path.GetFullPath(files[i].Path);
            paths.Add(normalized);
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0 && string.Equals(paths[i - 1], paths[i], StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"Torrent contains duplicate output path after sanitization: {paths[i]}");
            }

            if (i > 0 && IsAncestorPath(paths[i - 1], paths[i]))
            {
                throw new FormatException($"Torrent contains conflicting file and directory paths: {paths[i - 1]} and {paths[i]}");
            }
        }
    }

    private static bool IsAncestorPath(string possibleAncestor, string path)
    {
        string ancestor = possibleAncestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length <= ancestor.Length || !path.AsSpan().StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        char separator = path[ancestor.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static string JoinPath(string root, BList pathParts)
    {
        List<string> parts = [root];
        for (int i = 0; i < pathParts.Values.Count; i++)
        {
            parts.Add(CleanPathSegment(pathParts.Values[i].AsText("file.path[]")));
        }

        return Path.Combine(parts.ToArray());
    }

    private static string CleanPathSegment(string value)
    {
        string cleaned = value.Trim();
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
        {
            throw new FormatException("Torrent contains an unsafe empty path segment.");
        }

        if (IsAllDots(cleaned) || cleaned.EndsWith('.'))
        {
            throw new FormatException("Torrent contains an unsafe dot-normalized path segment.");
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalid.Length; i++)
        {
            cleaned = cleaned.Replace(invalid[i], '_');
        }

        if (IsReservedWindowsName(cleaned))
        {
            throw new FormatException("Torrent contains a reserved Windows path segment.");
        }

        return cleaned;
    }

    private static bool IsAllDots(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsReservedWindowsName(string value)
    {
        int dotIndex = value.IndexOf('.');
        ReadOnlySpan<char> stem = dotIndex >= 0 ? value.AsSpan(0, dotIndex) : value.AsSpan();
        stem = stem.TrimEnd('.');
        for (int i = 0; i < ReservedWindowsNames.Length; i++)
        {
            if (stem.Equals(ReservedWindowsNames[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Describes one file inside a torrent payload.
/// </summary>
/// <param name="Path">Relative output path for the file.</param>
/// <param name="Length">Length of the file in bytes.</param>
/// <param name="Offset">Byte offset of the file within the torrent payload.</param>
public sealed record TorrentFileEntry(string Path, long Length, long Offset);

internal readonly record struct PeerEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}
