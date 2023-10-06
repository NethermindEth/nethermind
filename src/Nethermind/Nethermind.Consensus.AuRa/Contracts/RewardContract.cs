// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IRewardContract : IActivatedAtBlock
    {
        /// <summary>
        /// produce rewards for the given benefactors,
        /// with corresponding reward codes.
        /// only callable by `SYSTEM_ADDRESS`
        /// function reward(address[] benefactors, uint16[] kind) external returns (address[], uint256[]);
        /// </summary>
        /// <param name="blockHeader"></param>
        /// <param name="benefactors">benefactor addresses</param>
        /// <param name="kind">
        /// Kind:
        /// 0 - Author - Reward attributed to the block author
        /// 2 - Empty step - Reward attributed to the author(s) of empty step(s) included in the block (AuthorityRound engine)
        /// 3 - External - Reward attributed by an external protocol (e.g. block reward contract)
        /// 101-106 - Uncle - Reward attributed to uncles, with distance 1 to 6 (Ethash engine)
        /// </param>
        (Address[] Addresses, UInt256[] Rewards) Reward(BlockHeader blockHeader, Address[] benefactors, ushort[] kind);
    }

    public sealed class RewardContract : CallableContract, IRewardContract
    {
        public long Activation { get; }

        public RewardContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, long transitionBlock)
            : base(transactionProcessor, abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Activation = transitionBlock;
        }

        /// <summary>
        /// produce rewards for the given benefactors,
        /// with corresponding reward codes.
        /// only callable by `SYSTEM_ADDRESS`
        /// function reward(address[] benefactors, uint16[] kind) external returns (address[], uint256[]);
        /// </summary>
        /// <param name="blockHeader"></param>
        /// <param name="benefactors">benefactor addresses</param>
        /// <param name="kind">
        /// Kind:
        /// 0 - Author - Reward attributed to the block author
        /// 2 - Empty step - Reward attributed to the author(s) of empty step(s) included in the block (AuthorityRound engine)
        /// 3 - External - Reward attributed by an external protocol (e.g. block reward contract)
        /// 101-106 - Uncle - Reward attributed to uncles, with distance 1 to 6 (Ethash engine)
        /// </param>
        public (Address[] Addresses, UInt256[] Rewards) Reward(BlockHeader blockHeader, Address[] benefactors, ushort[] kind)
        {
            var result = Call(blockHeader, nameof(Reward), Address.SystemUser, UnlimitedGas, benefactors, kind);
            return ((Address[])result[0], (UInt256[])result[1]);
        }
    }
}
