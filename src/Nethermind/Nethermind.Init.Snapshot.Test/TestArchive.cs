// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Formats.Tar;
using ZstdSharp;

namespace Nethermind.Init.Snapshot.Test;

internal static class TestArchive
{
    public static byte[] BuildTarZst(IReadOnlyDictionary<string, byte[]> files)
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "test" }));
            WriteFiles(writer, files);
        }

        return Compress(tarBuffer);
    }

    /// <summary>
    /// An uncompressed tar of <paramref name="files"/> under <c>db/</c>. With <paramref name="endWithLink"/> a link entry
    /// follows the files, so an extraction writes them all and then fails. <paramref name="emptyDirectory"/> adds an entry
    /// for a directory with nothing in it.
    /// </summary>
    public static byte[] BuildTar(IReadOnlyDictionary<string, byte[]> files, bool endWithLink = false, string? emptyDirectory = null)
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            WriteFiles(writer, files);
            if (emptyDirectory is not null)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"db/{emptyDirectory}/"));
            if (endWithLink)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "db/escape") { LinkName = "/tmp" });
        }

        return tarBuffer.ToArray();
    }

    /// <summary>A zip of <paramref name="files"/>, named as they are, without a top-level directory.</summary>
    public static byte[] BuildZip(IReadOnlyDictionary<string, byte[]> files)
    {
        using MemoryStream zipBuffer = new();
        using (System.IO.Compression.ZipArchive zip = new(zipBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in files)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(data);
            }
        }

        return zipBuffer.ToArray();
    }

    public static byte[] BuildTar(int payloadSize = 1000)
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "data"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data/state.bin")
            {
                DataStream = new MemoryStream(new byte[payloadSize])
            });
        }

        return tarBuffer.ToArray();
    }

    public static byte[] BuildTarWithoutTopLevelDirectory()
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "state.bin")
            {
                DataStream = new MemoryStream(new byte[1000])
            });
        }

        return tarBuffer.ToArray();
    }

    public static byte[] BuildTarEscapingDestination()
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "data/../escaped.bin")
            {
                DataStream = new MemoryStream(new byte[1000])
            });
        }

        return tarBuffer.ToArray();
    }

    public static byte[] BuildTarZstWithoutTopLevelDirectory()
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, "state.bin") { DataStream = new MemoryStream(new byte[10_000]) };
            writer.WriteEntry(entry);
        }

        return Compress(tarBuffer);
    }

    public static byte[] BuildTarZstWithSymlink()
    {
        using MemoryStream tarBuffer = new();
        using (TarWriter writer = new(tarBuffer, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "db"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "db/escape") { LinkName = "/tmp" });
        }

        return Compress(tarBuffer);
    }

    public static Dictionary<string, byte[]> BuildFiles(int seed = 42)
    {
        Random random = new(seed);
        byte[] state = new byte[50_000];
        byte[] headers = new byte[30_000];
        random.NextBytes(state);
        random.NextBytes(headers);
        return new Dictionary<string, byte[]>
        {
            [$"state/a{seed}.sst"] = state,
            [$"headers/b{seed}.sst"] = headers,
        };
    }

    private static void WriteFiles(TarWriter writer, IReadOnlyDictionary<string, byte[]> files)
    {
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "db"));
        HashSet<string> directories = [];
        foreach (string name in files.Keys)
        {
            string? parent = Path.GetDirectoryName(name);
            if (!string.IsNullOrEmpty(parent) && directories.Add(parent))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"db/{parent}"));
        }

        foreach ((string name, byte[] data) in files)
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, $"db/{name}") { DataStream = new MemoryStream(data) };
            writer.WriteEntry(entry);
        }
    }

    private static byte[] Compress(MemoryStream tarBuffer)
    {
        tarBuffer.Position = 0;
        using MemoryStream compressed = new();
        using (CompressionStream zstd = new(compressed, leaveOpen: true))
            tarBuffer.CopyTo(zstd);
        return compressed.ToArray();
    }
}
