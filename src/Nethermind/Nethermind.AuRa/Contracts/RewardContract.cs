using System;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;

namespace Nethermind.AuRa.Contracts
{
    public class RewardContract
    {
        private IAbiEncoder _abiEncoder;
        private readonly Address _contractAddress;

        public RewardContract(IAbiEncoder abiEncoder, Address contractAddress)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _contractAddress = contractAddress;
            _abiEncoder = abiEncoder;
        }
    }
}