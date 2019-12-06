using System.Collections.Generic;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
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

        public IReadOnlyList<Hash32> Proof => _proof.AsReadOnly();

        public override string ToString()
        {           
            return $"I:{Proof[^1].ToString().Substring(0, 12)} P:{Data.PublicKey.ToString().Substring(0, 12)} A:{Data.Amount}";
        }
    }
}
