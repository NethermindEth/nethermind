// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.Trie
{
    public class TreeDumper(bool expectAccounts = true) : INodeRecordVisitor<OldStyleTrieVisitContext>
    {
        private readonly StringBuilder _builder = new();
        public bool ExpectAccounts => expectAccounts;

        public void Reset() => _builder.Clear();

        public bool IsFullDbScan { get; init; } = true;

        public bool ShouldVisit(in OldStyleTrieVisitContext _, in ValueHash256 nextNode) => true;

        public void VisitTree(in OldStyleTrieVisitContext context, in ValueHash256 rootHash)
        {
            if (rootHash == Keccak.EmptyTreeHash)
            {
                _builder.AppendLine("EMPTY TREE");
            }
            else
            {
                _builder.AppendLine(context.IsStorage ? "STORAGE TREE" : "STATE TREE");
            }
        }

        private static string GetPrefix(in OldStyleTrieVisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : string.Empty, $"{GetChildIndex(context)}");

        private static string GetIndent(int level) => new('+', level * 2);
        private static string GetChildIndex(in OldStyleTrieVisitContext context) => context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";

        public void VisitMissingNode(in OldStyleTrieVisitContext context, in ValueHash256 nodeHash) => _builder.AppendLine($"{GetIndent(context.Level)}{GetChildIndex(context)}MISSING {nodeHash}");

        /// <inheritdoc/>
        public void VisitBranch(in OldStyleTrieVisitContext context, NodeRecordRef node) => _builder.AppendLine($"{GetPrefix(context)}BRANCH | -> {KeccakOrRlpStringOfNode(node)}");

        /// <inheritdoc/>
        public void VisitExtension(in OldStyleTrieVisitContext context, NodeRecordRef node) => _builder.AppendLine($"{GetPrefix(context)}EXTENSION {KeyStringOfNode(node)} -> {KeccakOrRlpStringOfNode(node)}");

        /// <inheritdoc/>
        public void VisitLeaf(in OldStyleTrieVisitContext context, NodeRecordRef node)
        {
            if (!expectAccounts)
            {
                _builder.AppendLine($"{GetPrefix(context)}LEAF {KeyStringOfNode(node)} -> {KeccakOrRlpStringOfNode(node)}");
            }
        }

        /// <inheritdoc/>
        public void VisitAccount(in OldStyleTrieVisitContext context, NodeRecordRef node, in AccountStruct account)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(context)}{leafDescription} {KeyStringOfNode(node)} -> {KeccakOrRlpStringOfNode(node)}");
            RlpReader valueReader = new(node.LeafValue);
            if (!context.IsStorage)
            {
                _builder.AppendLine($"{GetPrefix(context)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(context)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(context)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(context)}  VALUE: {valueReader.DecodeByteArray().ToHexString(true, true)}");
            }
        }

        public override string ToString() => _builder.ToString();

        private static string KeccakOrRlpStringOfNode(NodeRecordRef node) =>
            node.Hash != ValueKeccak.Zero ? node.Hash.Bytes.ToHexString() : node.Rlp.ToHexString();

        private static string KeyStringOfNode(NodeRecordRef node)
        {
            Span<byte> key = stackalloc byte[node.KeyLength(0)];
            node.CopyKey(0, key);
            return key.ToHexString(false);
        }
    }
}
