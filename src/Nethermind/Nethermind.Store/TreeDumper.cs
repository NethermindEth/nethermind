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

using System;
using System.Text;
using Nethermind.Core.Crypto;
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
        
        public void VisitTree(ValueKeccak rootHash, VisitContext context)
        {
//            if (rootHash == Keccak.EmptyTreeHash)
//            {
//                _builder.AppendLine("EMPTY TREEE");
//            }
//            else
//            {
//                _builder.AppendLine(context.IsStorage ? "STORAGE TREE" : "STATE TREE");
//            }
            throw new NotImplementedException();
        }
        
        private string GetPrefix(VisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : "", $"{GetChildIndex(context)}");
        
        private string GetIndent(int level) => new string('+', level * 2);
        private string GetChildIndex(VisitContext context) => context.BranchChildIndex == null ? string.Empty : $"{context.BranchChildIndex:00} ";
        
        public void VisitMissingNode(ValueKeccak nodeHash, VisitContext context)
        {
            //_builder.AppendLine($"{GetIndent(context.Level) }{GetChildIndex(context)}MISSING {nodeHash}");
            throw new NotImplementedException();
        }

        public void VisitBranch(Span<byte> hashOrRlp, VisitContext context)
        {
            //_builder.AppendLine($"{GetPrefix(context)}BRANCH {hashOrRlp?.ToHexString()}");
            throw new NotImplementedException();
        }

        public void VisitExtension(Span<byte> hashOrRlp, VisitContext context)
        {
            //_builder.AppendLine($"{GetPrefix(context)}EXTENSION {hashOrRlp?.ToHexString()}");
            throw new NotImplementedException();
        }

        public void VisitLeaf(Span<byte> hashOrRlp, VisitContext context)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            //_builder.AppendLine($"{GetPrefix(context)}{leafDescription}{hashOrRlp?.ToHexString()}");
            throw new NotImplementedException();
        }

        public void VisitCode(Keccak codeHash, byte[] code, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}CODE {codeHash} LENGTH {code.Length}");
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }
}