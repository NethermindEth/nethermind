// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.Serializers;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class VerkleKeyValueDb: IVerkleDb, IVerkleKeyValueDb, IKeyValueStore
{
    private readonly IDbProvider _dbProvider;

    public IDb LeafDb => _dbProvider.LeafDb;
    public IDb StemDb => _dbProvider.StemDb;
    public IDb BranchDb => _dbProvider.BranchDb;

    public VerkleKeyValueDb(IDbProvider dbProvider)
    {
        _dbProvider = dbProvider;
    }

    public byte[]? GetLeaf(byte[] key) => LeafDb.Get(key);
    public SuffixTree? GetStem(byte[] key)
    {
        byte[]? value = StemDb[key];
        return value is null ? null : SuffixTreeSerializer.Instance.Decode(value);
    }

    public InternalNode? GetBranch(byte[] key)
    {
        byte[]? value = BranchDb[key];
        return value is null ? null : InternalNodeSerializer.Instance.Decode(value);
    }

    public bool GetLeaf(byte[] key, out byte[]? value)
    {
        value = GetLeaf(key);
        return value is not null;
    }
    public bool GetStem(byte[] key, out SuffixTree? value)
    {
        value = GetStem(key);
        return value is not null;
    }
    public bool GetBranch(byte[] key, out InternalNode? value)
    {
        value = GetBranch(key);
        return value is not null;
    }

    public void SetLeaf(byte[] leafKey, byte[] leafValue) => _setLeaf(leafKey, leafValue, LeafDb);
    public void SetStem(byte[] stemKey, SuffixTree suffixTree) => _setStem(stemKey, suffixTree, StemDb);
    public void SetBranch(byte[] branchKey, InternalNode internalNodeValue) => _setBranch(branchKey, internalNodeValue, BranchDb);

    public void RemoveLeaf(byte[] leafKey)
    {
        LeafDb.Remove(leafKey);
    }
    public void RemoveStem(byte[] stemKey)
    {
        StemDb.Remove(stemKey);
    }
    public void RemoveBranch(byte[] branchKey)
    {
        BranchDb.Remove(branchKey);
    }


    public void BatchLeafInsert(IEnumerable<KeyValuePair<byte[], byte[]?>> keyLeaf)
    {
        using IBatch batch = LeafDb.StartBatch();
        foreach ((byte[] key, byte[]? value) in keyLeaf)
        {
            _setLeaf(key, value, batch);
        }
    }
    public void BatchStemInsert(IEnumerable<KeyValuePair<byte[], SuffixTree?>> suffixLeaf)
    {
        using IBatch batch = StemDb.StartBatch();
        foreach ((byte[] key, SuffixTree? value) in suffixLeaf)
        {
            _setStem(key, value, batch);
        }
    }
    public void BatchBranchInsert(IEnumerable<KeyValuePair<byte[], InternalNode?>> branchLeaf)
    {
        using IBatch batch = BranchDb.StartBatch();
        foreach ((byte[] key, InternalNode? value) in branchLeaf)
        {
            _setBranch(key, value, batch);
        }
    }

    public byte[]? this[byte[] key]
    {
        get
        {
            return key.Length switch
            {
                32 => LeafDb[key],
                31 => StemDb[key],
                _ => BranchDb[key]
            };
        }
        set
        {
            switch (key.Length)
            {
                case 32:
                    LeafDb[key] = value;
                    break;
                case 31:
                    StemDb[key] = value;
                    break;
                default:
                    BranchDb[key] = value;
                    break;
            }
        }
    }

    private static void _setLeaf(byte[] leafKey, byte[]? leafValue, IKeyValueStore db) => db[leafKey] = leafValue;
    private static void _setStem(byte[] stemKey, SuffixTree? suffixTree, IKeyValueStore db)
    {
        if (suffixTree != null) db[stemKey] = SuffixTreeSerializer.Instance.Encode(suffixTree).Bytes;
    }
    private static void _setBranch(byte[] branchKey, InternalNode? internalNodeValue, IKeyValueStore db)
    {
        if (internalNodeValue != null) db[branchKey] = InternalNodeSerializer.Instance.Encode(internalNodeValue).Bytes;
    }


}
