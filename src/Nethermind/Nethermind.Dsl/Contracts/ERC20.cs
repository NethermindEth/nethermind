//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Facade;

namespace Nethermind.Dsl.Contracts
{
    public class ERC20 : BlockchainBridgeContract
    {
        private IConstantContract ConstantContract { get; }
        private readonly IBlockTree _blockTree;
        private readonly INethermindApi _api;
        
        public ERC20(Address contractAddress, INethermindApi api) : base(contractAddress)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _blockTree = _api.BlockTree;
            ContractAddress = contractAddress;
            var blockchainBridge = _api.CreateBlockchainBridge(); 
            ConstantContract = GetConstant(blockchainBridge);
        }

        public ushort decimals()
        {
            var result = ConstantContract.Call<byte>(_blockTree.Head.Header, nameof(decimals), Address.Zero);
            var bytes = new [] {result};
            return BitConverter.ToUInt16(bytes);
        }   
    }
}