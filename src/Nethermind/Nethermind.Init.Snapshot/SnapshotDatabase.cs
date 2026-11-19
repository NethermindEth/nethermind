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
            foreach (string entry in Entries(dbPath))
            {
                // Neither the marker nor an empty linked directory, which is the operator's disk layout, is database content.
                if (!IsMarker(entry) && (!IsLinkedDirectory(entry) || Entries(entry).Any()))
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
    /// Claims the database for an extraction before the extraction writes anything: only this claim lets a later start
    /// delete the database content.
    /// </summary>
    public static void BeginExtraction(string dbPath)
    {
        ThrowIfUnavailableLink(dbPath);
        Directory.CreateDirectory(dbPath);
        using FileStream marker = new(MarkerPath(dbPath), FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0, FileOptions.WriteThrough);
        marker.Write(ExtractionMarkerContent);
        marker.Flush(flushToDisk: true);
    }

    /// <summary>Releases the claim after a successful extraction. A failure must stop the start.</summary>
    public static void CompleteExtraction(string dbPath) => File.Delete(MarkerPath(dbPath));

    /// <summary>Whether an extraction claimed the database and did not finish.</summary>
    /// <exception cref="IOException">
    /// A file has the marker's name but not its content, so it shows nothing; or the database directory is a link whose
    /// target is missing, so the marker cannot be read.
    /// </exception>
    public static bool HasUnfinishedExtraction(string dbPath)
    {
        ThrowIfUnavailableLink(dbPath);
        string markerPath = MarkerPath(dbPath);
        if (!Path.Exists(markerPath))
            return false;

        try
        {
            if (File.ReadAllBytes(markerPath).AsSpan().SequenceEqual(ExtractionMarkerContent))
                return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw UnrecognizedMarker(markerPath, e);
        }

        throw UnrecognizedMarker(markerPath, null);
    }

    /// <summary>
    /// Deletes what an unfinished extraction wrote, then its marker. Directory links stay, so the next extraction lands
    /// on the same disks: the database directory itself when it is a link, and linked subdirectories, whose targets are
    /// emptied instead. A link at the top of the database directory whose target is missing or loops, a linked
    /// subdirectory whose target cannot be read, or a directory link deeper in the content stops the cleanup with the
    /// marker kept. <c>lost+found</c> stays in the database directory and in linked targets.
    /// </summary>
    public static void Delete(string dbPath)
    {
        if (!Directory.Exists(dbPath))
            return;

        ThrowIfEntryLinkDoesNotResolve(dbPath);

        foreach (string entry in Entries(dbPath).ToArray())
        {
            if (IsMarker(entry))
                continue;

            if (IsLinkedDirectory(entry))
            {
                foreach (string nested in LinkedContent(entry))
                    DeleteEntry(nested);
            }
            else
            {
                DeleteEntry(entry);
            }
        }

        // Last: a cleanup that stops early keeps the claim, so the next start cleans up again.
        File.Delete(MarkerPath(dbPath));

        if (!IsLinkedDirectory(dbPath))
            TryRemoveDirectoryItself(dbPath);
    }

    private static void DeleteEntry(string entry)
    {
        if (!Directory.Exists(entry))
        {
            // A file, or a link that does not lead to a directory: removed itself, never followed. Links at the top of the
            // database directory, which route data to disks, were checked before the cleanup began.
            File.Delete(entry);
        }
        else if (IsLink(entry))
        {
            // The extractor never creates links (tar link entries are rejected, zip ones are written as regular files),
            // so it did not create this one: it is neither removed nor followed.
            throw new SnapshotLinkException(
                $"The database contains the directory link {(SensitiveLogMasking.Enabled ? "[redacted]" : entry)}, which a snapshot extraction does not create. Remove the link or empty the database directory, then restart.", null);
        }
        else
        {
            foreach (string nested in Directory.GetFileSystemEntries(entry))
                DeleteEntry(nested);
            Directory.Delete(entry);
        }
    }

    private static string MarkerPath(string dbPath) => Path.Combine(dbPath, ExtractionMarker);

    private static bool IsMarker(string entry) => Path.GetFileName(entry) == ExtractionMarker;

    // The marker behind such a link cannot be read, and the directory cannot be created in its place (mkdir refuses), so
    // the start stops here with an error that names the link rather than later with a less clear one.
    private static void ThrowIfUnavailableLink(string dbPath)
    {
        if (!Directory.Exists(dbPath) && IsUnresolvedLink(dbPath))
            throw UnavailableLink(dbPath, null);
    }

    private static IOException UnrecognizedMarker(string markerPath, Exception? inner) =>
        new($"The file {(SensitiveLogMasking.Enabled ? "[redacted]" : markerPath)} has the name of the snapshot extraction marker but not its content, so it does not show that the database holds an unfinished extraction, and nothing was deleted. Remove the file; if the database holds a partial extraction, empty the database directory too.",
            SensitiveLogMasking.Enabled ? null : inner);

    private static IEnumerable<string> Entries(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).Where(static entry => Path.GetFileName(entry) != MountArtifact);

    // A linked directory as the drive list (DbDriveInfoProvider) defines it.
    private static bool IsLinkedDirectory(string path) => Directory.Exists(path) && IsLink(path);

    // A trailing separator would hide the link.
    private static bool IsLink(string path) => new DirectoryInfo(Path.TrimEndingDirectorySeparator(path)).LinkTarget is not null;

    // A link whose final target is missing, or a link loop. File.Exists cannot tell: it is true for a dangling link. A
    // relative target is joined to the link's directory as text, so that directory must not be a link itself.
    private static bool IsUnresolvedLink(string path)
    {
        FileSystemInfo? target;
        try
        {
            target = File.ResolveLinkTarget(Path.TrimEndingDirectorySeparator(path), returnFinalTarget: true);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false; // Nothing is at the path.
        }
        catch (IOException)
        {
            return true; // Too many levels of links.
        }

        return target is not null && !File.Exists(target.FullName) && !Directory.Exists(target.FullName);
    }

    // The links at the top of the database directory route data to disks: one that does not resolve may lead to a disk that
    // is not mounted, and removing it would put the next extraction on the disk that holds the link. Checked before anything
    // is deleted, so a link to a file the cleanup removes first is not taken for a missing one; and through the database
    // directory's own target, so a relative ../ link resolves as on disk.
    private static void ThrowIfEntryLinkDoesNotResolve(string dbPath)
    {
        string directory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(dbPath)).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dbPath;
        foreach (string entry in Entries(directory))
        {
            if (!Directory.Exists(entry) && IsUnresolvedLink(entry))
                throw UnavailableLink(Path.Combine(dbPath, Path.GetFileName(entry)), null);
        }
    }

    private static string[] LinkedContent(string link)
    {
        try
        {
            return Entries(link).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw UnavailableLink(link, e);
        }
    }

    private static SnapshotLinkException UnavailableLink(string link, Exception? inner) =>
        new($"The database link {(SensitiveLogMasking.Enabled ? "[redacted]" : link)} points at a target that is missing or cannot be read, so it is neither removed nor replaced. Mount the linked disk or fix the link, then restart.",
            SensitiveLogMasking.Enabled ? null : inner);

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

/// <summary>The cleanup stopped at a link it must neither remove nor follow; the message names the link and the way out.</summary>
internal sealed class SnapshotLinkException(string message, Exception? innerException) : IOException(message, innerException);
