using Paprika.Crypto;
using Paprika.Data;

namespace Paprika;

public interface IBatch : IReadOnlyBatch
{
    /// <summary>
    /// Sets the metadata of the root of the current batch.
    /// </summary>
    void SetMetadata(uint blockNumber, in Keccak blockHash);

    /// <summary>
    /// Sets data raw.
    /// </summary>
    void SetRaw(in Key key, ReadOnlySpan<byte> rawData);

    /// <summary>
    /// Marks the given account as destroyed.
    /// </summary>
    /// <exception cref="NotSupportedException">Always: it would map the account to ContractId 0, shared by every destroyed account.</exception>
    void Destroy(in NibblePath account);

    /// <summary>
    /// Deletes all the keys that share the given prefix.
    /// </summary>
    void DeleteByPrefix(in Key prefix);

    /// <summary>
    /// Deletes every key, in constant time.
    /// </summary>
    /// <remarks>
    /// The pages of the deleted state are reused only once no reader or retained root can reach them, so readers opened
    /// before keep reading it. Contract ids keep counting up and are never reused.
    /// </remarks>
    void Clear();

    /// <summary>
    /// Commits the block returning its root hash.
    /// </summary>
    /// <param name="options">How to commit.</param>
    /// <returns>The state root hash.</returns>
    /// <remarks>
    /// A batch is committed at most once: afterwards, reading from it, writing to it or committing it again throws
    /// <see cref="ObjectDisposedException"/>. Any exception poisons the database, so that no later batch builds on a
    /// state whose persistence is unknown: <see cref="IDb.BeginNextBatch"/> throws until the database is reopened.
    /// A batch whose <see cref="SetRaw"/> or <see cref="DeleteByPrefix"/> threw cannot be committed: this throws
    /// <see cref="InvalidOperationException"/> without poisoning the database, and the batch is to be disposed.
    /// </remarks>
    ValueTask Commit(CommitOptions options);

    /// <summary>
    /// Performs a time-consuming verification when <see cref="Commit"/> is called that all the pages are reachable.
    /// </summary>
    void VerifyDbPagesOnCommit();
}

public enum CommitOptions
{
    /// <summary>
    /// Flushes db only once, ensuring that the data are stored properly.
    /// The root is stored ephemerally, waiting for the next commit to be truly stored.
    ///
    /// This guarantees ATOMIC (from ACID) but not DURABLE as the last root may not be flushed properly.
    /// It will be during the next flush.
    /// </summary>
    FlushDataOnly,

    /// <summary>
    /// Flush data, then the root.
    ///
    /// This guarantees ATOMIC and DURABLE (from ACID).
    /// </summary>
    FlushDataAndRoot,

    /// <summary>
    /// No actual flush happens and the database may become corrupted when the program is interrupted.
    /// </summary>
    DangerNoFlush,

    /// <summary>
    /// No write to file. Everything is in-memory only.
    /// </summary>
    DangerNoWrite,
}
