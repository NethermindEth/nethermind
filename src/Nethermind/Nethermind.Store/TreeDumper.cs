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
            _builder.AppendLine($"{GetIndent(context.Level) }{GetChildIndex(context)}MISSING {nodeHash}");
        }

        public void VisitBranch(byte[] hashOrRlp, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}BRANCH {hashOrRlp?.ToHexString()}");
        }

        public void VisitExtension(byte[] hashOrRlp, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}EXTENSION {hashOrRlp?.ToHexString()}");
        }

        public void VisitLeaf(byte[] hashOrRlp, VisitContext context)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(context)}{leafDescription}{hashOrRlp?.ToHexString()}");
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