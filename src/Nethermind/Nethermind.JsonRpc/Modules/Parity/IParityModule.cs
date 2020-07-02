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
        ResultWrapper<ParityTransaction[]> parity_pendingTransactions();
        ResultWrapper<ReceiptForRpc[]> parity_getBlockReceipts(BlockParameter blockParameter);
        ResultWrapper<string> parity_enode();
        ResultWrapper<bool> parity_setEngineSigner(Address address, string password);
        ResultWrapper<bool> parity_setEngineSignerSecret(string privateKey);
        ResultWrapper<bool> parity_clearEngineSigner();
    }
}
