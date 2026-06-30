// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.Logging;
using Nethermind.StateDiffArchive.Storage;

namespace Nethermind.StateDiffArchive.Merging;

/// <summary>
/// Merges several state-diff archive directories (each covering a different, disjoint block range) into a
/// single archive directory.
/// </summary>
/// <remarks>
/// Because the ranges are disjoint, merging is a file/slot-level union of the era files: an era held by
/// exactly one source is copied verbatim; an era held by several sources (only the boundary files where two
/// ranges share an 8192-block window) is merged slot by slot. Block records themselves are never decoded.
/// </remarks>
public static class StateDiffMerger
{
    private const string FileExtension = "diff";

    public static MergeResult Merge(IReadOnlyList<string> sourceDirectories, string outputDirectory, ILogger logger)
    {
        Directory.CreateDirectory(outputDirectory);

        SortedDictionary<ulong, List<string>> eraSources = CollectEras(sourceDirectories, logger);

        MergeResult result = new();
        bool any = false;
        foreach ((ulong era, List<string> dirs) in eraSources)
        {
            string outputPath = SlotStore.FilePath(outputDirectory, era, FileExtension);
            if (dirs.Count == 1)
            {
                File.Copy(SlotStore.FilePath(dirs[0], era, FileExtension), outputPath, overwrite: true);
            }
            else
            {
                MergeEra(era, dirs, outputPath, logger);
            }

            using SlotFile merged = new(outputPath);
            Accumulate(merged, era, result, ref any);
        }

        result.Gaps = any ? result.LastBlock - result.FirstBlock + 1 - (ulong)result.BlocksMerged : 0;
        if (logger.IsInfo)
            logger.Info($"StateDiffArchive merge complete: {result.BlocksMerged} blocks " +
                        $"[{result.FirstBlock}..{result.LastBlock}] into {outputDirectory}; gaps: {result.Gaps}.");
        return result;
    }

    private static SortedDictionary<ulong, List<string>> CollectEras(IReadOnlyList<string> sourceDirectories, ILogger logger)
    {
        SortedDictionary<ulong, List<string>> eraSources = [];
        foreach (string dir in sourceDirectories)
        {
            if (!Directory.Exists(dir))
            {
                if (logger.IsWarn) logger.Warn($"StateDiffArchive merge: source directory '{dir}' does not exist; skipping.");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, $"*.{FileExtension}"))
            {
                if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file), out ulong era)) continue;
                if (!eraSources.TryGetValue(era, out List<string>? dirs)) eraSources[era] = dirs = [];
                dirs.Add(dir);
            }
        }
        return eraSources;
    }

    // Unions one era's slots across the sources into that era's output file. Disjoint ranges (plus the
    // first-wins dedup below) mean the result holds at most one full era — the same 8192 blocks a single
    // recorder would produce — so it stays within a normal era file's bounds.
    private static void MergeEra(ulong era, List<string> dirs, string outputPath, ILogger logger)
    {
        using SlotFile target = new(outputPath);
        foreach (string dir in dirs)
        {
            using SlotFile source = new(SlotStore.FilePath(dir, era, FileExtension));
            for (int slot = 0; slot < SlotFile.SlotsPerFile; slot++)
            {
                if (!source.HasSlot(slot)) continue;
                if (target.HasSlot(slot))
                {
                    if (logger.IsWarn) logger.Warn($"StateDiffArchive merge: block {era * SlotFile.SlotsPerFile + (ulong)slot} present in multiple sources; keeping the first.");
                    continue;
                }
                source.TryRead(slot, static (data, st) => st.target.TryWrite(st.slot, data), (target, slot));
            }
        }
    }

    private static void Accumulate(SlotFile file, ulong era, MergeResult result, ref bool any)
    {
        ulong baseBlock = era * SlotFile.SlotsPerFile;
        for (int slot = 0; slot < SlotFile.SlotsPerFile; slot++)
        {
            if (!file.HasSlot(slot)) continue;
            ulong block = baseBlock + (ulong)slot;
            if (!any) { result.FirstBlock = block; any = true; }
            if (block > result.LastBlock) result.LastBlock = block;
            result.BlocksMerged++;
        }
    }

    public sealed class MergeResult
    {
        public ulong FirstBlock { get; set; }
        public ulong LastBlock { get; set; }
        public long BlocksMerged { get; set; }
        public ulong Gaps { get; set; }
        public bool Contiguous => Gaps == 0;
    }
}
