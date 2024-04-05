// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie
{
    public class TreeDumper : ITreeVisitor
    {
        private readonly StringBuilder _builder = new();

        public void Reset()
        {
            _builder.Clear();
        }

        public bool IsFullDbScan => true;

        public bool ShouldVisit(Hash256 nextNode)
        {
            return true;
        }

        public void VisitTree(Hash256 rootHash, TrieVisitContext trieVisitContext)
        {
            if (rootHash == Keccak.EmptyTreeHash)
            {
                _builder.AppendLine("EMPTY TREE");
            }
            else
            {
                _builder.AppendLine(trieVisitContext.IsStorage ? "STORAGE TREE" : "STATE TREE");
            }
        }

        private static string GetPrefix(TrieVisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : string.Empty, $"{GetChildIndex(context)}");

        private static string GetIndent(int level) => new('+', level * 2);
        private static string GetChildIndex(TrieVisitContext context) => context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";

        public void VisitMissingNode(Hash256 nodeHash, TrieVisitContext trieVisitContext)
        {
            _builder.AppendLine($"{GetIndent(trieVisitContext.Level)}{GetChildIndex(trieVisitContext)}MISSING {nodeHash}");
        }

        public void VisitBranch(TrieNode node, TrieVisitContext trieVisitContext)
        {
            _builder.AppendLine($"{GetPrefix(trieVisitContext)}BRANCH | -> {KeccakOrRlpStringOfNode(node)}");
        }

        public void VisitExtension(TrieNode node, TrieVisitContext trieVisitContext)
        {
            _builder.AppendLine($"{GetPrefix(trieVisitContext)}EXTENSION {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
        }

        private readonly AccountDecoder decoder = new();

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, ReadOnlySpan<byte> value)
        {
            string leafDescription = trieVisitContext.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(trieVisitContext)}{leafDescription} {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
            Rlp.ValueDecoderContext valueDecoderContext = new(value);
            if (!trieVisitContext.IsStorage)
            {
                Account account = decoder.Decode(ref valueDecoderContext);
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  VALUE: {valueDecoderContext.DecodeByteArray().ToHexString(true, true)}");
            }
        }

        public void VisitCode(Hash256 codeHash, TrieVisitContext trieVisitContext)
        {
            _builder.AppendLine($"{GetPrefix(trieVisitContext)}CODE {codeHash}");
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
