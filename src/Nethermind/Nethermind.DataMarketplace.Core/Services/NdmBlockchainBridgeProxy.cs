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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmBlockchainBridgeProxy : INdmBlockchainBridge  
    {
        private readonly IEthJsonRpcClientProxy _proxy;

        public NdmBlockchainBridgeProxy(IEthJsonRpcClientProxy proxy)
        {
            _proxy = proxy;
        }
        
        public Task<long> GetLatestBlockNumberAsync()
        {
            throw new System.NotImplementedException();
        }

        public Task<byte[]> GetCodeAsync(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Task<Block> FindBlockAsync(Keccak blockHash)
        {
            throw new System.NotImplementedException();
        }

        public Task<Block> FindBlockAsync(long blockNumber)
        {
            throw new System.NotImplementedException();
        }

        public Task<Block> GetLatestBlockAsync()
        {
            throw new System.NotImplementedException();
        }


        public Task<UInt256> GetNonceAsync(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Task<NdmTransaction> GetTransactionAsync(Keccak transactionHash)
        {
            throw new System.NotImplementedException();
        }

        public Task<int> GetNetworkIdAsync()
        {
            throw new System.NotImplementedException();
        }

        public Task<byte[]> CallAsync(Transaction transaction)
        {
            throw new System.NotImplementedException();
        }

        public Task<byte[]> CallAsync(Transaction transaction, long blockNumber)
        {
            throw new System.NotImplementedException();
        }

        public Task<byte[]> CallAsync(Transaction transaction, Keccak blockHash)
        {
            throw new System.NotImplementedException();
        }

        public Task<Keccak> SendOwnTransactionAsync(Transaction transaction)
        {
            throw new System.NotImplementedException();
        }

        public Account GetAccount(Address address)
        {
            throw new System.NotImplementedException();
        }

        public void Sign(Transaction transaction)
        {
            throw new System.NotImplementedException();
        }
    }
}