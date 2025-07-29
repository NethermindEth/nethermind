// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie
{
    public class TreeDumper : ITreeVisitor<OldStyleTrieVisitContext>
    {
        private readonly StringBuilder _builder = new();

        public void Reset()
        {
            _builder.Clear();
        }

        public bool IsFullDbScan { get; init; } = true;

        public bool ShouldVisit(in OldStyleTrieVisitContext _, in ValueHash256 nextNode)
        {
            return true;
        }

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

        public void VisitMissingNode(in OldStyleTrieVisitContext context, in ValueHash256 nodeHash)
        {
            _builder.AppendLine($"{GetIndent(context.Level)}{GetChildIndex(context)}MISSING {nodeHash}");
        }

        public void VisitBranch(in OldStyleTrieVisitContext context, TrieNode node)
        {
            _builder.AppendLine($"{GetPrefix(context)}BRANCH | -> {KeccakOrRlpStringOfNode(node)}");
        }

        public void VisitExtension(in OldStyleTrieVisitContext context, TrieNode node)
        {
            _builder.AppendLine($"{GetPrefix(context)}EXTENSION {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
        }

        public void VisitLeaf(in OldStyleTrieVisitContext context, TrieNode node)
        {
        }

        public void VisitAccount(in OldStyleTrieVisitContext context, TrieNode node, in AccountStruct account)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(context)}{leafDescription} {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
            Rlp.ValueDecoderContext valueDecoderContext = new(node.Value);
            if (!context.IsStorage)
            {
                _builder.AppendLine($"{GetPrefix(context)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(context)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(context)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(context)}  VALUE: {valueDecoderContext.DecodeByteArray().ToHexString(true, true)}");
            }
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private static string? KeccakOrRlpStringOfNode(TrieNode node)
        {
            return node.Keccak is not null ? node.Keccak!.Bytes.ToHexString() : node.FullRlp.AsSpan().ToHexString();
        }
    }
}
