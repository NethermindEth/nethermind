using System.Collections.Generic;

namespace Cortex.Containers
{
    public class Deposit
    {
        private const int DEPOSIT_CONTRACT_TREE_DEPTH = 2 ^ 5; // 32

        private readonly List<Hash32> _proof;

        public Deposit()
        {
            Data = new DepositData();
            _proof = new List<Hash32>();
            for (var index = 0; index < DEPOSIT_CONTRACT_TREE_DEPTH; index++)
            {
                _proof.Add(new Hash32());
            }
        }

        public DepositData Data { get; }

        public IReadOnlyList<Hash32> Proof { get { return _proof.AsReadOnly(); } }
    }
}
