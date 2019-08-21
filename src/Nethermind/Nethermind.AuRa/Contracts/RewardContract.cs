using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.AuRa.Contracts
{
    public class RewardContract : SystemContract
    {
        private readonly IAbiEncoder _abiEncoder;
        
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public static class Definition
        {
            
            /// <summary>
            /// produce rewards for the given benefactors,
            /// with corresponding reward codes.
            /// only callable by `SYSTEM_ADDRESS`
            /// function reward(address[] benefactors, uint16[] kind) external returns (address[], uint256[]);
            ///
            /// Kind:
            /// 0 - Author - Reward attributed to the block author
            /// 2 - Empty step - Reward attributed to the author(s) of empty step(s) included in the block (AuthorityRound engine)
            /// 3 - External - Reward attributed by an external protocol (e.g. block reward contract)
            /// 101-106 - Uncle - Reward attributed to uncles, with distance 1 to 6 (Ethash engine)
            /// </summary>
            public static readonly AbiEncodingInfo reward = new AbiEncodingInfo(AbiEncodingStyle.IncludeSignature,
                new AbiSignature(nameof(reward), new AbiArray(AbiType.Address), new AbiArray(AbiType.UInt16)));

            public static readonly AbiEncodingInfo rewardReturn = new AbiEncodingInfo(AbiEncodingStyle.None,
                new AbiSignature(nameof(rewardReturn), new AbiArray(AbiType.Address), new AbiArray(AbiType.UInt256)));
        }
        
        public RewardContract(IAbiEncoder abiEncoder, Address contractAddress) : base(contractAddress)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
        }
        
        public Transaction Reward(Address[] benefactors, ushort[] kind)
            => GenerateTransaction(_abiEncoder.Encode(Definition.reward, benefactors, kind));
        
        public (Address[] Addresses, BigInteger[] Rewards) DecodeRewards(byte[] data)
        {
            var objects = _abiEncoder.Decode(Definition.rewardReturn, data);
            return ((Address[]) objects[0], (BigInteger[]) objects[1]);
        }
    }
}