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
        
        public void VisitTree(Keccak rootHash, VisitContext context)
        {
            if (rootHash == Keccak.EmptyTreeHash)
            {
                _builder.AppendLine("EMPTY TREEE");
            }
            else
            {
                _builder.AppendLine(context.IsStorage ? "STORAGE TREE" : "STATE TREE");
            }
        }
        
        private string GetPrefix(VisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : "", $"{GetChildIndex(context)}");
        
        private string GetIndent(int level) => new string('+', level * 2);
        private string GetChildIndex(VisitContext context) => context.BranchChildIndex == null ? string.Empty : $"{context.BranchChildIndex:00} ";
        
        public void VisitMissingNode(Keccak nodeHash, VisitContext context)
        {
            _builder.AppendLine($"{GetIndent(context.Level) }{GetChildIndex(context)} MISSING {nodeHash}");
        }

        public void VisitBranch(byte[] hashOrRlp, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}BRANCH {hashOrRlp?.ToHexString()}");
        }

        public void VisitExtension(byte[] hashOrRlp, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}EXTENSION {hashOrRlp?.ToHexString()}");
        }

        private AccountDecoder decoder = new AccountDecoder();
        
        public void VisitLeaf(byte[] hashOrRlp, VisitContext context, byte[] value = null)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(context)}{leafDescription}{hashOrRlp?.ToHexString()}");
            if (!context.IsStorage)
            {
                Account account = decoder.Decode(new Rlp.DecoderContext(value));
                _builder.AppendLine($"{GetPrefix(context)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(context)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(context)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(context)}  VALUE: {new Rlp.DecoderContext(value).DecodeByteArray().ToHexString()}");
            }
        }

        public void VisitCode(Keccak codeHash, byte[] code, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)} CODE {codeHash} LENGTH {code.Length}");
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }
}