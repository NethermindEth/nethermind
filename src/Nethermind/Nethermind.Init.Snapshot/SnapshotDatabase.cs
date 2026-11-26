// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

internal static class SnapshotDatabase
{
    private const string MountArtifact = "lost+found";

    // In the database directory, so it stays with the data when the snapshot directory and its checkpoint change or are lost.
    private const string ExtractionMarker = ".nethermind-snapshot-extracting";

    // How an exclusive create reports that its path exists: ERROR_FILE_EXISTS on Windows, EEXIST on Linux and macOS.
    private const int FileExistsHResult = unchecked((int)0x80070050);
    private const int FileExistsErrno = 17;

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
    /// <exception cref="IOException">
    /// The database holds content or another extraction's marker, or the marker could not be created or written.
    /// </exception>
    public static void BeginExtraction(string dbPath)
    {
        // Checked here too: content or another extraction can appear during a long download, and the extraction would write
        // over it. As at admission, the content is read before the marker: an extraction creates its marker first.
        if (Exists(dbPath))
            throw new IOException(HasUnfinishedExtraction(dbPath)
                ? UnfinishedExtraction(dbPath)
                : $"The database at {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} holds content when the snapshot extraction would begin, so nothing was extracted. Make sure no other node is using it. To initialize from the snapshot, empty the database directory, then restart.");

        Directory.CreateDirectory(dbPath);
        FileStream marker;
        try
        {
            marker = new(MarkerPath(dbPath), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.WriteThrough);
        }
        catch (Exception e) when (IsMarkerCollision(e, dbPath))
        {
            // Another extraction created its marker after the content check. Other failures stay as they are.
            throw new IOException(UnfinishedExtraction(dbPath), SensitiveLogMasking.Enabled ? null : e);
        }

        using (marker)
        {
            marker.Write(ExtractionMarkerContent);
            marker.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Deletes the file at the marker's path, which <see cref="BeginExtraction"/> created, after the extraction succeeded,
    /// and nothing else. A failure must stop the start.
    /// </summary>
    public static void CompleteExtraction(string dbPath) => File.Delete(MarkerPath(dbPath));

    /// <summary>Whether anything is at the marker's path: an extraction began in the database directory and did not finish.</summary>
    /// <remarks>Only the metadata of whatever is there is read; it is not opened.</remarks>
    /// <exception cref="IOException">The marker's path could not be looked up, so whether it holds anything is unknown.</exception>
    public static bool HasUnfinishedExtraction(string dbPath)
    {
        // Below a file at the database path, which counts as content, the lookup fails as not found.
        try
        {
            _ = File.GetAttributes(MarkerPath(dbPath));
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Such as a database directory the node may not search: a marker in it would go unseen.
            throw new IOException(
                $"Could not check the database directory {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} for the snapshot extraction marker {ExtractionMarker}, so the snapshot initialization stops. Make sure the node may read the directory, then restart. Error: {(SensitiveLogMasking.Enabled ? e.GetType().Name : e.Message)}",
                SensitiveLogMasking.Enabled ? null : e);
        }
    }

    /// <summary>What the marker shows and what the operator does about it; the step leaves the directory as it is.</summary>
    public static string UnfinishedExtraction(string dbPath) =>
        $"The database directory {(SensitiveLogMasking.Enabled ? "[redacted]" : dbPath)} holds the snapshot extraction marker {ExtractionMarker}: an extraction into it began and was not completed, or is still running in another process. The snapshot initialization leaves the directory as it is. Make sure no node or container is using the directory and let a running extraction finish. Otherwise empty the directory by hand, the hidden marker included (move aside anything you do not recognise), or configure a new empty database directory, then restart.";

    /// <summary>
    /// Whether an archive entry, given by its full path under <paramref name="destinationRoot"/> (which ends with a
    /// separator), would write where the extraction must not: below the top-level <c>lost+found</c>, which stays the
    /// filesystem's, at <c>lost+found</c> itself unless it is a directory entry, which the extraction skips (see
    /// <see cref="IsRecoveryDirectory"/>), or at the marker's path.
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

    /// <summary>
    /// Whether an archive entry, given by its full path under <paramref name="destinationRoot"/> (which ends with a
    /// separator), is the top-level <c>lost+found</c> itself. A dump of a mount root holds its directory entry; the
    /// extraction skips it, so it neither creates <c>lost+found</c> nor fails on whatever the filesystem has there.
    /// </summary>
    public static bool IsRecoveryDirectory(string destinationRoot, string entryPath)
    {
        ReadOnlySpan<char> path = Path.TrimEndingDirectorySeparator(entryPath.AsSpan());
        return path.Length == destinationRoot.Length + MountArtifact.Length
            && path.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(MountArtifact, StringComparison.OrdinalIgnoreCase);
    }

    private static string MarkerPath(string dbPath) => Path.Combine(dbPath, ExtractionMarker);

    // How an exclusive create reports an entry already at the marker path: as "file exists" (EEXIST, for an entry of any
    // kind, on Unix; ERROR_FILE_EXISTS for a file on Windows), or on Windows as access denied for a directory there. Any
    // other failure, a permission error with no marker included, is not a collision.
    private static bool IsMarkerCollision(Exception e, string dbPath) => e switch
    {
        UnauthorizedAccessException => OperatingSystem.IsWindows() && Directory.Exists(MarkerPath(dbPath)),
        IOException => e.HResult == (OperatingSystem.IsWindows() ? FileExistsHResult : FileExistsErrno) && HasUnfinishedExtraction(dbPath),
        _ => false,
    };
}
