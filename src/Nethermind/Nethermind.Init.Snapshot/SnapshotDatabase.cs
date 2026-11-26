// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

internal static class SnapshotDatabase
{
    private const string MountArtifact = "lost+found";

    // In the database directory, so it stays with the data when the snapshot directory and its checkpoint change or are lost.
    private const string ExtractionMarker = ".nethermind-snapshot-extracting";

    private static ReadOnlySpan<byte> ExtractionMarkerContent => "Nethermind snapshot extraction in progress"u8;

    public static bool Exists(string dbPath)
    {
        if (File.Exists(dbPath))
            return true;
        if (!Directory.Exists(dbPath))
            return false;

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(dbPath))
            {
                // lost+found is the filesystem's, skipped by name and never opened: the extraction writes nothing into it.
                string name = Path.GetFileName(entry);
                if (name != ExtractionMarker && name != MountArtifact)
                    return true;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Marks the database directory before an extraction writes anything to it, so that a later start finds the extraction
    /// unfinished instead of taking it for a database.
    /// </summary>
    /// <exception cref="IOException">The database holds content, or the marker could not be created.</exception>
    public static void BeginExtraction(string dbPath)
    {
        // Checked here too: content can appear during a long download, and the extraction would write over it.
        if (Exists(dbPath))
            throw new IOException($"The database at {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} holds content when the snapshot extraction would begin, so nothing was extracted. To initialize from the snapshot, empty the database directory, then restart.");

        Directory.CreateDirectory(dbPath);
        using FileStream marker = new(MarkerPath(dbPath), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.WriteThrough);
        marker.Write(ExtractionMarkerContent);
        marker.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Deletes the file at the marker's path, which <see cref="BeginExtraction"/> created, after the extraction succeeded,
    /// and nothing else. A failure must stop the start.
    /// </summary>
    public static void CompleteExtraction(string dbPath) => File.Delete(MarkerPath(dbPath));

    /// <summary>Whether anything is at the marker's path: an extraction began in the database directory and did not finish.</summary>
    /// <remarks>Whatever is there is neither opened nor read.</remarks>
    public static bool HasUnfinishedExtraction(string dbPath)
    {
        string markerPath = MarkerPath(dbPath);
        return File.Exists(markerPath) || Directory.Exists(markerPath);
    }

    /// <summary>What the operator does about an unfinished extraction, which the step leaves in place.</summary>
    public static string UnfinishedExtraction(string dbPath) =>
        $"The database directory {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} holds a snapshot extraction that did not finish: the marker {ExtractionMarker} and whatever the extraction wrote before it stopped. The snapshot initialization deletes nothing in the database directory. Make sure no node is using the directory, delete its contents by hand, then restart to initialize from the snapshot again.";

    public static void Delete(string dbPath)
    {
        if (!Directory.Exists(dbPath))
            return;

        foreach (string entry in Directory.GetFileSystemEntries(dbPath))
        {
            if (Path.GetFileName(entry) == MountArtifact)
                continue;

            if (Directory.Exists(entry))
                Directory.Delete(entry, true);
            else
                File.Delete(entry);
        }

        TryRemoveDirectoryItself(dbPath);
    }

    /// <summary>
    /// Whether an archive entry, given by its full path under <paramref name="destinationRoot"/> (which ends with a
    /// separator), would write where the extraction must not: below the top-level <c>lost+found</c>, which stays the
    /// filesystem's, at <c>lost+found</c> itself unless it is a directory, or at the marker's path.
    /// </summary>
    public static bool IsReservedDestination(string destinationRoot, string entryPath, bool isDirectory)
    {
        ReadOnlySpan<char> path = Path.TrimEndingDirectorySeparator(entryPath.AsSpan());
        if (!path.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        ReadOnlySpan<char> relative = path[destinationRoot.Length..];
        if (relative.Equals(ExtractionMarker, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!relative.StartsWith(MountArtifact, StringComparison.OrdinalIgnoreCase))
            return false;

        ReadOnlySpan<char> rest = relative[MountArtifact.Length..];
        return rest.IsEmpty ? !isDirectory : rest[0] == Path.DirectorySeparatorChar;
    }

    private static string MarkerPath(string dbPath) => Path.Combine(dbPath, ExtractionMarker);

    private static bool TryRemoveDirectoryItself(string dbPath)
    {
        try
        {
            Directory.Delete(dbPath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
