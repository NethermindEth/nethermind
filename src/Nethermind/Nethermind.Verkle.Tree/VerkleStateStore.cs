using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree;

public class VerkleStateStore : IVerkleStore, ISyncTrieStore
{
    public byte[] RootHash
    {
        get => GetStateRoot();
        set => MoveToStateRoot(value);
    }
    // the blockNumber for with the fullState exists.
    private long FullStateBlock { get; set; }

    // The underlying key value database
    // We try to avoid fetching from this, and we only store at the end of a batch insert
    private VerkleKeyValueDb Storage { get; }

    // This stores the key-value pairs that we need to insert into the storage. This is generally
    // used to batch insert changes for each block. This is also used to generate the forwardDiff.
    // This is flushed after every batch insert and cleared.
    private VerkleMemoryDb Batch { get; set; }

    private VerkleHistoryStore History { get; }

    private IDb StateRootToBlocks { get; }

    public VerkleStateStore(IDbProvider dbProvider)
    {
        Storage = new VerkleKeyValueDb(dbProvider);
        Batch = new VerkleMemoryDb();
        History = new VerkleHistoryStore(dbProvider);
        StateRootToBlocks = dbProvider.StateRootToBlocks;
        FullStateBlock = 0;
        InitRootHash();
    }

    public ReadOnlyVerkleStateStore AsReadOnly(VerkleMemoryDb keyValueStore)
    {
        return new ReadOnlyVerkleStateStore(this, keyValueStore);
    }

    // This generates and returns a batchForwardDiff, that can be used to move the full state from fromBlock to toBlock.
    // for this fromBlock < toBlock - move forward in time
    public VerkleMemoryDb GetForwardMergedDiff(long fromBlock, long toBlock)
    {
        return History.GetBatchDiff(fromBlock, toBlock).DiffLayer;
    }

    // This generates and returns a batchForwardDiff, that can be used to move the full state from fromBlock to toBlock.
    // for this fromBlock > toBlock - move back in time
    public VerkleMemoryDb GetReverseMergedDiff(long fromBlock, long toBlock)
    {
        return History.GetBatchDiff(fromBlock, toBlock).DiffLayer;
    }
    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    private void InitRootHash()
    {
        if(Batch.GetBranch(Array.Empty<byte>(), out InternalNode? _)) return;
        Batch.SetBranch(Array.Empty<byte>(), new BranchNode());
    }

    public byte[]? GetLeaf(byte[] key)
    {
#if DEBUG
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
#endif
        if (Batch.GetLeaf(key, out byte[]? value)) return value;
        return Storage.GetLeaf(key, out value) ? value : null;
    }

    public SuffixTree? GetStem(byte[] stemKey)
    {
#if DEBUG
        if (stemKey.Length != 31) throw new ArgumentException("stem must be 31 bytes", nameof(stemKey));
#endif
        if (Batch.GetStem(stemKey, out SuffixTree? value)) return value;
        return Storage.GetStem(stemKey, out value) ? value : null;
    }

    public InternalNode? GetBranch(byte[] key)
    {
        if (Batch.GetBranch(key, out InternalNode? value)) return value;
        return Storage.GetBranch(key, out value) ? value : null;
    }

    public void SetLeaf(byte[] leafKey, byte[] leafValue)
    {
#if DEBUG
        if (leafKey.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(leafKey));
        if (leafValue.Length != 32) throw new ArgumentException("value must be 32 bytes", nameof(leafValue));
#endif
        Batch.SetLeaf(leafKey, leafValue);
    }

    public void SetStem(byte[] stemKey, SuffixTree suffixTree)
    {
#if DEBUG
        if (stemKey.Length != 31) throw new ArgumentException("stem must be 32 bytes", nameof(stemKey));
#endif
        Batch.SetStem(stemKey, suffixTree);
    }

    public void SetBranch(byte[] branchKey, InternalNode internalNodeValue)
    {
        Batch.SetBranch(branchKey, internalNodeValue);
    }

    // This method is called at the end of each block to flush the batch changes to the storage and generate forward and reverse diffs.
    // this should be called only once per block, right now it does not support multiple calls for the same block number.
    // if called multiple times, the full state would be fine - but it would corrupt the diffs and historical state will be lost
    // TODO: add capability to update the diffs instead of overwriting if Flush(long blockNumber) is called multiple times for the same block number
    public void Flush(long blockNumber)
    {
        // we should not have any null values in the Batch db - because deletion of values from verkle tree is not allowed
        // nullable values are allowed in MemoryStateDb only for reverse diffs.
        VerkleMemoryDb reverseDiff = new VerkleMemoryDb();

        foreach (KeyValuePair<byte[], byte[]?> entry in Batch.LeafTable)
        {
            Debug.Assert(entry.Value is not null, "nullable value only for reverse diff");
            if (Storage.GetLeaf(entry.Key, out byte[]? node)) reverseDiff.LeafTable[entry.Key] = node;
            else reverseDiff.LeafTable[entry.Key] = null;

            Storage.SetLeaf(entry.Key, entry.Value);
        }

        foreach (KeyValuePair<byte[], SuffixTree?> entry in Batch.StemTable)
        {
            Debug.Assert(entry.Value is not null, "nullable value only for reverse diff");
            if (Storage.GetStem(entry.Key, out SuffixTree? node)) reverseDiff.StemTable[entry.Key] = node;
            else reverseDiff.StemTable[entry.Key] = null;

            Storage.SetStem(entry.Key, entry.Value);
        }

        foreach (KeyValuePair<byte[], InternalNode?> entry in Batch.BranchTable)
        {
            Debug.Assert(entry.Value is not null, "nullable value only for reverse diff");
            if (Storage.GetBranch(entry.Key, out InternalNode? node)) reverseDiff.BranchTable[entry.Key] = node;
            else reverseDiff.BranchTable[entry.Key] = null;

            Storage.SetBranch(entry.Key, entry.Value);
        }

        History.InsertDiff(blockNumber, Batch, reverseDiff);
        FullStateBlock = blockNumber;
        StateRootToBlocks.Set(GetBranch(Array.Empty<byte>())?._internalCommitment.PointAsField.ToBytes().ToArray() ?? throw new InvalidOperationException(), blockNumber.ToBigEndianByteArrayWithoutLeadingZeros());

        Batch = new VerkleMemoryDb();
    }

    // now the full state back in time by one block.
    public void ReverseState()
    {
        VerkleMemoryDb reverseDiff = History.GetBatchDiff(FullStateBlock, FullStateBlock - 1).DiffLayer;

        foreach (KeyValuePair<byte[], byte[]?> entry in reverseDiff.LeafTable)
        {
            reverseDiff.GetLeaf(entry.Key, out byte[]? node);
            if (node is null)
            {
                Storage.RemoveLeaf(entry.Key);
            }
            else
            {
                Storage.SetLeaf(entry.Key, node);
            }
        }

        foreach (KeyValuePair<byte[], SuffixTree?> entry in reverseDiff.StemTable)
        {
            reverseDiff.GetStem(entry.Key, out SuffixTree? node);
            if (node is null)
            {
                Storage.RemoveStem(entry.Key);
            }
            else
            {
                Storage.SetStem(entry.Key, node);
            }
        }

        foreach (KeyValuePair<byte[], InternalNode?> entry in reverseDiff.BranchTable)
        {
            reverseDiff.GetBranch(entry.Key, out InternalNode? node);
            if (node is null)
            {
                Storage.RemoveBranch(entry.Key);
            }
            else
            {
                Storage.SetBranch(entry.Key, node);
            }
        }
        FullStateBlock -= 1;
    }

    // use the batch diff to move the full state back in time to access historical state.
    public void ApplyDiffLayer(BatchChangeSet changeSet)
    {
        if (changeSet.FromBlockNumber != FullStateBlock) throw new ArgumentException($"Cannot apply diff FullStateBlock:{FullStateBlock}!=fromBlock:{changeSet.FromBlockNumber}", nameof(changeSet.FromBlockNumber));

        VerkleMemoryDb reverseDiff = changeSet.DiffLayer;

        foreach (KeyValuePair<byte[], byte[]?> entry in reverseDiff.LeafTable)
        {
            reverseDiff.GetLeaf(entry.Key, out byte[]? node);
            if (node is null)
            {
                Storage.RemoveLeaf(entry.Key);
            }
            else
            {
                Storage.SetLeaf(entry.Key, node);
            }
        }

        foreach (KeyValuePair<byte[], SuffixTree?> entry in reverseDiff.StemTable)
        {
            reverseDiff.GetStem(entry.Key, out SuffixTree? node);
            if (node is null)
            {
                Storage.RemoveStem(entry.Key);
            }
            else
            {
                Storage.SetStem(entry.Key, node);
            }
        }

        foreach (KeyValuePair<byte[], InternalNode?> entry in reverseDiff.BranchTable)
        {
            reverseDiff.GetBranch(entry.Key, out InternalNode? node);
            if (node is null)
            {
                Storage.RemoveBranch(entry.Key);
            }
            else
            {
                Storage.SetBranch(entry.Key, node);
            }
        }
        FullStateBlock = changeSet.ToBlockNumber;
    }
    public bool IsFullySynced(Keccak stateRoot)
    {
        return false;
    }

    public byte[] GetStateRoot()
    {
        return GetBranch(Array.Empty<byte>())?._internalCommitment.Point.ToBytes().ToArray() ?? throw new InvalidOperationException();
    }

    public InternalNode? GetRootNode()
    {
        return GetBranch(Array.Empty<byte>());
    }

    public void MoveToStateRoot(byte[] stateRoot)
    {
        byte[] currentRoot = GetBranch(Array.Empty<byte>())?._internalCommitment.PointAsField.ToBytes().ToArray() ?? throw new InvalidOperationException();

        if (currentRoot.SequenceEqual(stateRoot)) return;
        if (Keccak.EmptyTreeHash.Equals(stateRoot)) return;

        byte[]? fromBlockBytes = StateRootToBlocks[currentRoot];
        byte[]? toBlockBytes = StateRootToBlocks[stateRoot];
        if (fromBlockBytes is null) return;
        if (toBlockBytes is null) return;

        long fromBlock = fromBlockBytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros();
        long toBlock = toBlockBytes.ToLongFromBigEndianByteArrayWithoutLeadingZeros();

        ApplyDiffLayer(History.GetBatchDiff(fromBlock, toBlock));

        Debug.Assert(GetStateRoot().Equals(stateRoot));
    }
}

public interface IVerkleStore: IStoreWithReorgBoundary
{
    public byte[] RootHash { get; set; }
    byte[]? GetLeaf(byte[] key);
    SuffixTree? GetStem(byte[] key);
    InternalNode? GetBranch(byte[] key);
    void SetLeaf(byte[] leafKey, byte[] leafValue);
    void SetStem(byte[] stemKey, SuffixTree suffixTree);
    void SetBranch(byte[] branchKey, InternalNode internalNodeValue);
    void Flush(long blockNumber);
    void ReverseState();
    void ApplyDiffLayer(BatchChangeSet changeSet);

    byte[] GetStateRoot();
    void MoveToStateRoot(byte[] stateRoot);

    public ReadOnlyVerkleStateStore AsReadOnly(VerkleMemoryDb keyValueStore);

    public VerkleMemoryDb GetForwardMergedDiff(long fromBlock, long toBlock);

    public VerkleMemoryDb GetReverseMergedDiff(long fromBlock, long toBlock);

}
