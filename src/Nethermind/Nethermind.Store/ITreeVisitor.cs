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
        
        void VisitBranch(byte[] hashOrRlp, VisitContext context);
        
        void VisitExtension(byte[] hashOrRlp, VisitContext context);
        
        void VisitLeaf(byte[] hashOrRlp, VisitContext context);
        
        void VisitCode(Keccak codeHash, byte[] code, VisitContext context);
    }
}