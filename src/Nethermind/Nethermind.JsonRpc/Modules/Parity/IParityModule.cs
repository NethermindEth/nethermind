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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Parity
{
    [RpcModule(ModuleType.Parity)]
    public interface IParityModule : IModule
    {
        [JsonRpcMethod(Description = "Returns a list of transactions currently in the queue.", Returns = "Array", IsImplemented = true)]
        ResultWrapper<ParityTransaction[]> parity_pendingTransactions();

        [JsonRpcMethod(Description = "Get receipts from all transactions from particular block, more efficient than fetching the receipts one-by-one.", Returns = "Array", IsImplemented = true)]
        ResultWrapper<ReceiptForRpc[]> parity_getBlockReceipts(BlockParameter blockParameter);

        [JsonRpcMethod(Description = "Returns the node enode URI.", Returns = "String", IsImplemented = true)]
        ResultWrapper<string> parity_enode();

        [JsonRpcMethod(Description = "", Returns = "Boolean", IsImplemented = true)]
        ResultWrapper<bool> parity_setEngineSigner(Address address, string password);
        [JsonRpcMethod(Description = "", Returns = "Boolean", IsImplemented = true)]
        ResultWrapper<bool> parity_setEngineSignerSecret(string privateKey);

        [JsonRpcMethod(Description = "", Returns = "Boolean", IsImplemented = true)]
        ResultWrapper<bool> parity_clearEngineSigner();
    }
}
