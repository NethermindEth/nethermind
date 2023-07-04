// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie
{
    public class TreeDumper : ITreeVisitor
    {
        private StringBuilder _builder = new();

        public void Reset()
        {
            _builder.Clear();
        }

        public bool IsFullDbScan => true;

        public bool ShouldVisit(Keccak nextNode)
        {
            return true;
        }

        public void VisitTree(Keccak rootHash, TrieVisitContext trieVisitContext)
        {
            if (rootHash == Keccak.EmptyTreeHash)
            {
                _builder.AppendLine("EMPTY TREEE");
            }
            else
            {
                _builder.AppendLine(trieVisitContext.IsStorage ? "STORAGE TREE" : "STATE TREE");
            }
        }

        private string GetPrefix(TrieVisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : string.Empty, $"{GetChildIndex(context)}");

        private string GetIndent(int level) => new('+', level * 2);
        private string GetChildIndex(TrieVisitContext context) => context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";

        public void VisitMissingNode(Keccak nodeHash, TrieVisitContext trieVisitContext)
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

        private AccountDecoder decoder = new();

        public void VisitLeaf(TrieNode node, TrieVisitContext trieVisitContext, byte[] value = null)
        {
            string leafDescription = trieVisitContext.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(trieVisitContext)}{leafDescription} {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
            if (!trieVisitContext.IsStorage)
            {
                Account account = decoder.Decode(new RlpStream(value));
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(trieVisitContext)}  VALUE: {new RlpStream(value).DecodeByteArray().ToHexString(true, true)}");
            }
        }

        public void VisitCode(Keccak codeHash, TrieVisitContext trieVisitContext)
        {
            _builder.AppendLine($"{GetPrefix(trieVisitContext)}CODE {codeHash}");
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private string? KeccakOrRlpStringOfNode(TrieNode node)
        {
            return node.Keccak != null ? node.Keccak!.Bytes.ToHexString() : node.FullRlp?.ToHexString();
        }
    }
}
