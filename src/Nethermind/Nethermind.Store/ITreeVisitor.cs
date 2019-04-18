using Nethermind.Core.Crypto;

namespace Nethermind.Store
{
    public class VisitContext
    {
        public int Level { get; set; }

        public bool IsStorage { get; set; }

        public int? BranchChildIndex { get; set; }
    }
    
    public interface ITreeVisitor
    {
        void VisitTree(Keccak rootHash, VisitContext context);
        
        void VisitMissingNode(Keccak nodeHash, VisitContext context);
        
        void VisitBranch(Keccak nodeHash, VisitContext context);
        
        void VisitExtension(Keccak nodeHash, VisitContext context);
        
        void VisitLeaf(Keccak nodeHash, VisitContext context);
        
        void VisitCode(Keccak codeHash, VisitContext context);
    }
}