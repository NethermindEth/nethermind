// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Xdc;

// TODO: use WriteBatch?
public sealed class XdcTrieCopier(SourceContext source, TargetContext target): ITreeVisitor<TreePathContextWithStorage>
{
    public bool IsFullDbScan => true;

    public int StateNodesCopied { get; private set; }
    public int CodeNodesCopied { get; private set; }

    public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;
    public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) => CopyStateNode(rootHash.ToCommitment());
    public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => CopyStateNode(node.Keccak);
    public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => CopyStateNode(node.Keccak);
    public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => CopyStateNode(node.Keccak);

    public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account)
    {
        if (account.HasStorage)
            CopyStateNode(account.StorageRoot.ToCommitment());

        if (account.HasCode)
            CopyCodeNode(account.CodeHash.ToCommitment());
    }

    public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) { }

    private void CopyStateNode(Hash256? key)
    {
        if (key is not {Bytes: var keyBytes})
            return;

        if (source.Db.Get(keyBytes) is not { } value)
            throw new Exception($"Unexpected empty value for state key {key}.");

        target.StateDb.Set(keyBytes, value);
        StateNodesCopied++;
    }

    private void CopyCodeNode(Hash256 key)
    {
        if (key is not {Bytes: var keyBytes})
            return;

        keyBytes = (byte[]) [..XdcSchema.CodePrefix, ..keyBytes];

        if (source.Db.Get(keyBytes) is not { } code)
            throw new Exception($"Unexpected empty value for code key {key}.");

        target.CodeDb.Set(keyBytes, code);
        CodeNodesCopied++;
    }
}
