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

        public override string ToString()
        {           
            return $"I:{Proof[Proof.Count - 1].ToString().Substring(0, 8)} P:{Data.PublicKey.ToString().Substring(0, 16)} A:{Data.Amount}";
        }
    }
}
