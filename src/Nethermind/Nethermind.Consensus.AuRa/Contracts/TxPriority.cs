//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>
    /// Permission contract for <see cref="ITxPool"/> transaction ordering
    /// <seealso cref="https://github.com/poanetwork/posdao-contracts/blob/tx-priority/contracts/TxPriority.sol"/> 
    /// </summary>
    public class TxPermission : Contract
    {
        private ConstantContract Constant { get; }
        
        public TxPermission(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource) 
            : base(abiEncoder, contractAddress)
        {
            Constant = GetConstant(readOnlyTransactionProcessorSource);
        }

        public Address[] GetSendersWhitelist(BlockHeader parentHeader) => Constant.Call<Address[]>(parentHeader, nameof(GetSendersWhitelist), Address.Zero);
        
        public Address[] GetSendersWhitelist(BlockHeader parentHeader) => Constant.Call<Address[]>(parentHeader, nameof(GetSendersWhitelist), Address.Zero);
    }
}
