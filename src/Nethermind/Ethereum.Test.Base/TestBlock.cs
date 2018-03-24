namespace Ethereum.Test.Base
{
    public class TestBlock
    {
        public TestBlockHeader BlockHeader { get; set; }
        public TestBlockHeader[] UncleHeaders { get; set; }
        public string Rlp { get; set; }
        public IncomingTransaction[] Transactions { get; set; }
        public string ExpectedException { get; set; }
    }
}