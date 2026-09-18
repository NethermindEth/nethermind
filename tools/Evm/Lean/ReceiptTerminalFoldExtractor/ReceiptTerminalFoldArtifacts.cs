// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

internal static class ReceiptTerminalFoldArtifacts
{
    internal static void Publish(
        string irPath, byte[] ir,
        string leanPath, byte[] lean,
        string manifestPath, byte[] manifest,
        Action<int>? beforeReplace = null)
    {
        (string Path, byte[] Bytes)[] artifacts = [(irPath, ir), (leanPath, lean), (manifestPath, manifest)];
        string?[] staged = new string?[artifacts.Length];
        try
        {
            for (int index = 0; index < artifacts.Length; index++)
            {
                (string path, byte[] bytes) = artifacts[index];
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
                string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                    $".{Path.GetFileName(path)}.receipt-{Guid.NewGuid():N}.tmp");
                using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                staged[index] = temporary;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // The manifest is the last publication marker. An interrupted batch may
            // contain whole files from two generations, which exact --check rejects.
            for (int index = 0; index < artifacts.Length; index++)
            {
                if (staged[index] is not string temporary) continue;
                beforeReplace?.Invoke(index);
                string path = artifacts[index].Path;
                if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
                else File.Move(temporary, path);
            }
        }
        finally
        {
            foreach (string? temporary in staged)
                if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
