using Paprika.Crypto;
using Paprika.Store;

namespace Paprika;

public interface IDb
{
    /// <summary>
    /// Starts a db transaction that is for the next block.
    /// </summary>
    /// <returns>The transaction that handles block operations.</returns>
    IBatch BeginNextBatch();

    /// <summary>
    /// Starts a readonly batch that preserves a snapshot of the database as in the moment of its creation.
    /// </summary>
    /// <returns></returns>
    IReadOnlyBatch BeginReadOnlyBatch(string name = "");

    /// <summary>
    /// Performs a flush using <see cref="IPageManager.Flush"/>. 
    /// </summary>
    void Flush();

    /// <summary>
    /// Begins the readonly batch with the given Keccak or the latest.
    /// </summary>
    IReadOnlyBatch BeginReadOnlyBatchOrLatest(in Keccak stateHash, string name = "");

    /// <summary>
    /// Performs a snapshot of all the valid roots in the database.
    /// </summary>
    /// <returns>An array of roots.</returns>
    IReadOnlyBatch[] SnapshotAll();

    /// <summary>
    /// Whether there's a state with the given keccak.
    /// </summary>
    bool HasState(in Keccak keccak);

    /// <summary>
    /// Gets the history depth for the given db.
    /// </summary>
    int HistoryDepth { get; }

    void ForceFlush();
}
