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

        tarBuffer.Position = 0;
        using MemoryStream compressed = new();
        using (CompressionStream zstd = new(compressed, leaveOpen: true))
            tarBuffer.CopyTo(zstd);
        return compressed.ToArray();
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
}
