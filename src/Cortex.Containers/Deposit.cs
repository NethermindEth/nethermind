using System.Collections.Generic;

namespace Cortex.Containers
{
    public class Deposit
    {
        private readonly List<Hash32> _proof;

        public Deposit(IEnumerable<Hash32> proof, DepositData data)
        {
            _proof = new List<Hash32>(proof);
            Data = data;
        }

        public DepositData Data { get; }

        public IReadOnlyList<Hash32> Proof { get { return _proof.AsReadOnly(); } }
    }
}
