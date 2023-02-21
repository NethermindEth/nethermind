using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Db;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.VerkleDb;
using Nethermind.Verkle.Utils;
using Nethermind.Core.Extensions;

namespace Nethermind.Verkle.Tree;

public partial class VerkleTree
{
    public readonly IVerkleStore _stateDb;

    public byte[] RootHash
    {
        get => _stateDb.GetStateRoot();
        set => _stateDb.MoveToStateRoot(value);
    }

    public VerkleTree(IDbProvider dbProvider)
    {
        VerkleStateStore stateDb = new VerkleStateStore(dbProvider);
        _stateDb = new CompositeVerkleStateStore(stateDb);
    }

    protected VerkleTree(IVerkleStore stateStore)
    {
        _stateDb = new CompositeVerkleStateStore(stateStore);
    }

    private static Banderwagon GetLeafDelta(byte[]? oldValue, byte[] newValue, byte index)
    {
#if DEBUG
        if (oldValue is not null && oldValue.Length != 32) throw new ArgumentException("oldValue must be null or 32 bytes", nameof(oldValue));
        if (newValue.Length != 32) throw new ArgumentException("newValue must be 32 bytes", nameof(newValue));
#endif
        (FrE newValLow, FrE newValHigh) = VerkleUtils.BreakValueInLowHigh(newValue);
        (FrE oldValLow, FrE oldValHigh) = VerkleUtils.BreakValueInLowHigh(oldValue);

        int posMod128 = index % 128;
        int lowIndex = 2 * posMod128;
        int highIndex = lowIndex + 1;

        Banderwagon deltaLow = Committer.ScalarMul(newValLow - oldValLow, lowIndex);
        Banderwagon deltaHigh = Committer.ScalarMul(newValHigh - oldValHigh, highIndex);
        return deltaLow + deltaHigh;
    }

    public void Insert(Span<byte> key, byte[] value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
#if DEBUG
        if (key.Length != 32) throw new ArgumentException("key must be 32 bytes", nameof(key));
        if (value.Length != 32) throw new ArgumentException("value must be 32 bytes", nameof(value));
#endif
        LeafUpdateDelta leafDelta = UpdateLeaf(key, value);
        UpdateTreeCommitments(key[..31], leafDelta);
    }

    public IEnumerable<byte>? Get(byte[] key) => _stateDb.GetLeaf(key);

    public void InsertStemBatch(Span<byte> stem, Dictionary<byte, byte[]> leafIndexValueMap)
    {
#if DEBUG
        if (stem.Length != 31) throw new ArgumentException("stem must be 31 bytes", nameof(stem));
        Span<byte> key = new byte[32];
        foreach (KeyValuePair<byte, byte[]> keyVal in leafIndexValueMap)
        {
            stem.CopyTo(key);
            key[31] = keyVal.Key;
            Console.WriteLine("KA: " + EnumerableExtensions.ToString(key.ToArray()));
            Console.WriteLine("V: " + EnumerableExtensions.ToString(keyVal.Value));
        }
#endif

        LeafUpdateDelta leafDelta = UpdateLeaf(stem, leafIndexValueMap);
        UpdateTreeCommitments(stem, leafDelta);
    }

    private void UpdateTreeCommitments(Span<byte> stem, LeafUpdateDelta leafUpdateDelta)
    {
        // calculate this by update the leafs and calculating the delta - simple enough
        TraverseContext context = new TraverseContext(stem, leafUpdateDelta);
        Banderwagon rootDelta = TraverseBranch(context);
        UpdateRootNode(rootDelta);
    }

    private void UpdateRootNode(Banderwagon rootDelta)
    {
        InternalNode root = _stateDb.GetBranch(Array.Empty<byte>()) ?? throw new ArgumentException();
        root._internalCommitment.AddPoint(rootDelta);
        _stateDb.SetBranch(Array.Empty<byte>(), root);
    }

    private Banderwagon TraverseBranch(TraverseContext traverseContext)
    {
        byte childIndex = traverseContext.Stem[traverseContext.CurrentIndex];
        byte[] absolutePath = traverseContext.Stem[..(traverseContext.CurrentIndex + 1)].ToArray();

        InternalNode? child = GetBranchNode(absolutePath);
        if (child is null)
        {
            // 1. create new suffix node
            // 2. update the C1 or C2 - we already know the leafDelta - traverseContext.LeafUpdateDelta
            // 3. update ExtensionCommitment
            // 4. get the delta for commitment - ExtensionCommitment - 0;
            (FrE deltaHash, Commitment? suffixCommitment) = UpdateSuffixNode(traverseContext.Stem.ToArray(), traverseContext.LeafUpdateDelta, true);

            // 1. Add internal.stem node
            // 2. return delta from ExtensionCommitment
            _stateDb.SetBranch(absolutePath, new StemNode(traverseContext.Stem.ToArray(), suffixCommitment!));
            return Committer.ScalarMul(deltaHash, childIndex);
        }

        if (child.IsBranchNode)
        {
            traverseContext.CurrentIndex += 1;
            Banderwagon branchDeltaHash = TraverseBranch(traverseContext);
            traverseContext.CurrentIndex -= 1;
            FrE deltaHash = child.UpdateCommitment(branchDeltaHash);
            _stateDb.SetBranch(absolutePath, child);
            return Committer.ScalarMul(deltaHash, childIndex);
        }

        traverseContext.CurrentIndex += 1;
        (Banderwagon stemDeltaHash, bool changeStemToBranch) = TraverseStem(child, traverseContext);
        traverseContext.CurrentIndex -= 1;
        if (changeStemToBranch)
        {
            BranchNode newChild = new BranchNode();
            newChild._internalCommitment.AddPoint(child._internalCommitment.Point);
            // since this is a new child, this would be just the parentDeltaHash.PointToField
            // now since there was a node before and that value is deleted - we need to subtract
            // that from the delta as well
            FrE deltaHash = newChild.UpdateCommitment(stemDeltaHash);
            _stateDb.SetBranch(absolutePath, newChild);
            return Committer.ScalarMul(deltaHash, childIndex);
        }
        // in case of stem, no need to update the child commitment - because this commitment is the suffix commitment
        // pass on the update to upper level
        return stemDeltaHash;
    }

    private (Banderwagon, bool) TraverseStem(InternalNode node, TraverseContext traverseContext)
    {
        Debug.Assert(node.IsStem);

        (List<byte> sharedPath, byte? pathDiffIndexOld, byte? pathDiffIndexNew) =
            VerkleUtils.GetPathDifference(node.Stem, traverseContext.Stem.ToArray());

        if (sharedPath.Count != 31)
        {
            int relativePathLength = sharedPath.Count - traverseContext.CurrentIndex;
            // byte[] relativeSharedPath = sharedPath.ToArray()[traverseContext.CurrentIndex..].ToArray();
            byte oldLeafIndex = pathDiffIndexOld ?? throw new ArgumentException();
            byte newLeafIndex = pathDiffIndexNew ?? throw new ArgumentException();
            // node share a path but not the complete stem.

            // the internal node will be denoted by their sharedPath
            // 1. create SuffixNode for the traverseContext.Key - get the delta of the commitment
            // 2. set this suffix as child node of the BranchNode - get the commitment point
            // 3. set the existing suffix as the child - get the commitment point
            // 4. update the internal node with the two commitment points
            (FrE deltaHash, Commitment? suffixCommitment) = UpdateSuffixNode(traverseContext.Stem.ToArray(), traverseContext.LeafUpdateDelta, true);

            // creating the stem node for the new suffix node
            _stateDb.SetBranch(sharedPath.ToArray().Concat(new[]
            {
                newLeafIndex
            }).ToArray(), new StemNode(traverseContext.Stem.ToArray(), suffixCommitment!));
            Banderwagon newSuffixCommitmentDelta = Committer.ScalarMul(deltaHash, newLeafIndex);


            // instead on declaring new node here - use the node that is input in the function
            SuffixTree oldSuffixNode = _stateDb.GetStem(node.Stem) ?? throw new ArgumentException();
            _stateDb.SetBranch(sharedPath.ToArray().Concat(new[]
                {
                    oldLeafIndex
                }).ToArray(),
                new StemNode(node.Stem, oldSuffixNode.ExtensionCommitment));

            Banderwagon oldSuffixCommitmentDelta =
                Committer.ScalarMul(oldSuffixNode.ExtensionCommitment.PointAsField, oldLeafIndex);

            Banderwagon deltaCommitment = oldSuffixCommitmentDelta + newSuffixCommitmentDelta;

            Banderwagon internalCommitment = FillSpaceWithInternalBranchNodes(sharedPath.ToArray(), relativePathLength, deltaCommitment);

            return (internalCommitment - oldSuffixNode.ExtensionCommitment.Point, true);
        }

        (FrE deltaFr, Commitment updatedSuffixCommitment) = UpdateSuffixNode(traverseContext.Stem.ToArray(), traverseContext.LeafUpdateDelta);
        _stateDb.SetBranch(traverseContext.Stem[..traverseContext.CurrentIndex].ToArray(), new StemNode(node.Stem, updatedSuffixCommitment) );
        return (Committer.ScalarMul(deltaFr, traverseContext.Stem[traverseContext.CurrentIndex - 1]), false);
    }

    private Banderwagon FillSpaceWithInternalBranchNodes(byte[] path, int length, Banderwagon deltaPoint)
    {
        for (int i = 0; i < length; i++)
        {
            BranchNode newInternalNode = new BranchNode();
            FrE upwardsDelta = newInternalNode.UpdateCommitment(deltaPoint);
            _stateDb.SetBranch(path[..^i], newInternalNode);
            deltaPoint = Committer.ScalarMul(upwardsDelta, path[path.Length - i - 1]);
        }

        return deltaPoint;
    }

    private InternalNode? GetBranchNode(byte[] pathWithIndex) => _stateDb.GetBranch(pathWithIndex);

    private (FrE, Commitment) UpdateSuffixNode(byte[] stemKey, LeafUpdateDelta leafUpdateDelta, bool insertNew = false)
    {
        SuffixTree suffixNode = insertNew? new SuffixTree(stemKey): _stateDb.GetStem(stemKey)?? throw new ArgumentException();

        FrE deltaFr = suffixNode.UpdateCommitment(leafUpdateDelta);
        _stateDb.SetStem(stemKey, suffixNode);
        // add the init commitment, because while calculating diff, we subtract the initCommitment in new nodes.
        return insertNew ? (deltaFr + suffixNode.InitCommitmentHash, suffixNode.ExtensionCommitment.Dup()) : (deltaFr, suffixNode.ExtensionCommitment.Dup());
    }

    private LeafUpdateDelta UpdateLeaf(Span<byte> key, byte[] value)
    {
        LeafUpdateDelta leafDelta = new LeafUpdateDelta();
        leafDelta.UpdateDelta(_updateLeaf(key.ToArray(), value), key[31]);
        return leafDelta;
    }
    private LeafUpdateDelta UpdateLeaf(Span<byte> stem, Dictionary<byte, byte[]> indexValuePairs)
    {
        Span<byte> key = new byte[32];
        stem.CopyTo(key);
        LeafUpdateDelta leafDelta = new LeafUpdateDelta();
        foreach ((byte index, byte[] value) in indexValuePairs)
        {
            key[31] = index;
            leafDelta.UpdateDelta(_updateLeaf(key.ToArray(), value), key[31]);
        }
        return leafDelta;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Banderwagon _updateLeaf(byte[] key, byte[] value)
    {
        byte[]? oldValue = _stateDb.GetLeaf(key);
        Banderwagon leafDeltaCommitment = GetLeafDelta(oldValue, value, key[31]);
        _stateDb.SetLeaf(key, value);
        return leafDeltaCommitment;
    }

    public VerkleMemoryDb GetForwardMergedDiff(long fromBlock, long toBlock)
    {
        return _stateDb.GetForwardMergedDiff(fromBlock, toBlock);
    }

    public VerkleMemoryDb GetReverseMergedDiff(long fromBlock, long toBlock)
    {
        return _stateDb.GetReverseMergedDiff(fromBlock, toBlock);
    }

    public void Flush(long blockNumber)
    {
        _stateDb.Flush(blockNumber);
    }
    public void ReverseState()
    {
        _stateDb.ReverseState();
    }

    public void ApplyDiffLayer(VerkleMemoryDb reverseBatch, long fromBlock, long toBlock)
    {
        _stateDb.ApplyDiffLayer(new BatchChangeSet(fromBlock, toBlock, reverseBatch));
    }

    private ref struct TraverseContext
    {
        public LeafUpdateDelta LeafUpdateDelta { get; }
        public Span<byte> Stem { get; }
        public int CurrentIndex { get; set; }

        public TraverseContext(Span<byte> stem, LeafUpdateDelta delta)
        {
            Stem = stem;
            CurrentIndex = 0;
            LeafUpdateDelta = delta;
        }
    }

    public void GenerateVerkleProof(List<byte[]> keys)
    {
        foreach (byte[] key in keys)
        {

        }
    }

    public void FindKeyPath(byte[] key)
    {
        for (int i = 0; i < key.Length; i++)
        {

        }
    }
}
