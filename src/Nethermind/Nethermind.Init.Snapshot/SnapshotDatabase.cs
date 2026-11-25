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
                string name = Path.GetFileName(entry);
                if (name != ExtractionMarker && (name != MountArtifact || !IsEmptyDirectory(entry)))
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
    /// Whether an extracted entry, given by its full path, would land in the top-level <c>lost+found</c>: only an empty one
    /// counts as no content, and it stays the filesystem's.
    /// </summary>
    public static bool IsInRecoveryDirectory(string destinationRoot, string entryPath) =>
        Path.TrimEndingDirectorySeparator(entryPath).StartsWith(destinationRoot + MountArtifact + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string MarkerPath(string dbPath) => Path.Combine(dbPath, ExtractionMarker);

    // What a filesystem check saved in lost+found is content, and a link by that name may lead anywhere.
    private static bool IsEmptyDirectory(string path)
    {
        DirectoryInfo directory = new(path);
        if (directory.LinkTarget is not null || !directory.Exists)
            return false;

        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return !entries.MoveNext();
    }

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
