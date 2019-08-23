/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public class TreeDumper : ITreeVisitor
    {
        private StringBuilder _builder = new StringBuilder();

        public void Reset()
        {
            _builder.Clear();
        }
        
        public bool ShouldVisit(Keccak nextNode)
        {
            return true;
        }        
        
        public void VisitTree(Keccak rootHash, VisitContext visitContext)
        {
            if (rootHash == Keccak.EmptyTreeHash)
            {
                _builder.AppendLine("EMPTY TREEE");
            }
            else
            {
                _builder.AppendLine(visitContext.IsStorage ? "STORAGE TREE" : "STATE TREE");
            }
        }
        
        private string GetPrefix(VisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : "", $"{GetChildIndex(context)}");
        
        private string GetIndent(int level) => new string('+', level * 2);
        private string GetChildIndex(VisitContext context) => context.BranchChildIndex == null ? string.Empty : $"{context.BranchChildIndex:00} ";
        
        public void VisitMissingNode(Keccak nodeHash, VisitContext visitContext)
        {
            _builder.AppendLine($"{GetIndent(visitContext.Level) }{GetChildIndex(visitContext)}MISSING {nodeHash}");
        }

        public void VisitBranch(TrieNode node, VisitContext visitContext)
        {
            _builder.AppendLine($"{GetPrefix(visitContext)}BRANCH {(node.Keccak?.Bytes ?? node.FullRlp?.Bytes)?.ToHexString()}");
        }

        public void VisitExtension(TrieNode node, VisitContext visitContext)
        {
            _builder.AppendLine($"{GetPrefix(visitContext)}EXTENSION {(node.Keccak?.Bytes ?? node.FullRlp?.Bytes)?.ToHexString()}");
        }

        private AccountDecoder decoder = new AccountDecoder();
        
        public void VisitLeaf(TrieNode node, VisitContext visitContext, byte[] value = null)
        {
            string leafDescription = visitContext.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(visitContext)}{leafDescription}{(node.Keccak?.Bytes ?? node.FullRlp?.Bytes)?.ToHexString()}");
            if (!visitContext.IsStorage)
            {
                Account account = decoder.Decode(new RlpStream(value));
                _builder.AppendLine($"{GetPrefix(visitContext)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(visitContext)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(visitContext)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(visitContext)}  VALUE: {new RlpStream(value).DecodeByteArray().ToHexString(true, true)}");
            }
        }

        public void VisitCode(Keccak codeHash, byte[] code, VisitContext visitContext)
        {
            _builder.AppendLine($"{GetPrefix(visitContext)}CODE {codeHash} LENGTH {code.Length}");
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }
}