// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.TestSelection.Files;

namespace Nethermind.TestSelection.Journal;

/// <summary>
/// The journal is the source of truth: one line per finished run, oldest first. Everything else (the model, the
/// replay) is derived from it and can always be rebuilt.
/// </summary>
public static class RunJournal
{
    public static IReadOnlyList<JournalEntry> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        List<JournalEntry> entries = [];
        foreach (string line in JsonFiles.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                entries.Add(Normalize(JsonSerializer.Deserialize<JournalEntry>(line, JsonFiles.Options)!));
            }
        }

        entries.Sort(static (left, right) => left.At.CompareTo(right.At));
        return entries;
    }

    public static void Write(string path, IEnumerable<JournalEntry> entries) =>
        JsonFiles.WriteLines(path, entries.Select(static entry => JsonSerializer.Serialize(entry, JsonFiles.Options)));

    // A successful run has no failures; older journals wrote that as null.
    private static JournalEntry Normalize(JournalEntry entry) => entry.Failed is null ? entry with { Failed = Failures.None } : entry;
}
