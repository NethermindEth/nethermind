using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain
{
    public class InvalidBlockException : BlockchainException
    {
        public InvalidBlockException(Rlp rlp)
        {
            Rlp = rlp;
        }

        public Rlp Rlp { get; set; }
    }
}