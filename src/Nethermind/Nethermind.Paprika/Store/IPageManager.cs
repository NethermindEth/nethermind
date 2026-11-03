namespace Paprika.Store;

public interface IPageManager : IDisposable, IPageResolver
{
    /// <summary>
    /// The number of pages the manager holds.
    /// </summary>
    int MaxPage { get; }

    DbAddress GetAddress(in Page page);

    /// <summary>
    /// Gets the page for writing purposes, ensuring that the page that is requested is finalized on disk.
    /// </summary>
    Page GetAtForWriting(DbAddress address, bool reused);

    /// <summary>
    /// Writes pages specified by <paramref name="addresses"/> to the underlying storage.
    /// </summary>
    /// <remarks>
    /// If <paramref name="options"/> are <see cref="CommitOptions.FlushDataOnly"/> or
    /// <see cref="CommitOptions.FlushDataAndRoot"/>, the write operations are followed by a <see cref="Flush"/>.
    /// This ensures ACI in ACID. 
    /// </remarks>
    ValueTask WritePages(ICollection<DbAddress> addresses, CommitOptions options);

    /// <summary>
    /// Writes the specified <paramref name="rootPage"/> to the underlying storage.
    /// </summary>
    /// <remarks>
    /// If <paramref name="options"/> are <see cref="CommitOptions.FlushDataAndRoot"/>,
    /// the write operations are followed by a <see cref="Flush"/>.
    ///
    /// This ensures D in ACID.
    ///
    /// If no flush is done, it will be synchronized with a next commit. 
    /// </remarks>
    ValueTask WriteRootPage(DbAddress rootPage, CommitOptions options);

    /// <summary>
    /// Flushes buffers, using FSYNC or FlushFileBuffers.
    /// </summary>
    void Flush();

    /// <summary>
    /// First flushes the underlying view of the file, then, performs <see cref="Flush"/>.
    /// </summary>
    /// <remarks>
    /// This operation should be used only when working with the mem mapped file without issuing
    /// <see cref="WritePages"/> and <see cref="WriteRootPage"/>, for example in IMPORT scenario.
    /// </remarks>
    void ForceFlush();
}
