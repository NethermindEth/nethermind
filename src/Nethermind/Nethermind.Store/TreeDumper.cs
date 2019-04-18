using System.Text;
using Nethermind.Core.Crypto;

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

        public void VisitBranch(Keccak nodeHash, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}BRANCH {nodeHash}");
        }

        public void VisitExtension(Keccak nodeHash, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}EXTENSION {nodeHash}");
        }

        public void VisitLeaf(Keccak nodeHash, VisitContext context)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(context)}{leafDescription}{nodeHash}");
        }

        public void VisitCode(Keccak codeHash, VisitContext context)
        {
            _builder.AppendLine($"{GetPrefix(context)}CODE {codeHash}");
        }

        public override string ToString()
        {
            return _builder.ToString();
        }
    }
}