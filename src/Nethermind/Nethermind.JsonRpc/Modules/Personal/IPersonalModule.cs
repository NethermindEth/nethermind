/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Security.Cryptography.X509Certificates;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Personal
{
    public interface IPersonalModule : IModule
    {   
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<Address> personal_importRawKey(byte keyData, string passphrase);
        ResultWrapper<Address[]> personal_listAccounts();
        
        ResultWrapper<bool> personal_lockAccount(Address address);
        
        ResultWrapper<bool> personal_unlockAccount(Address address, string passphrase);
        
        ResultWrapper<Address> personal_newAccount(string passphrase);
        
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<Keccak> personal_sendTransaction(TransactionForRpc transaction, string passphrase);
     
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<Address> personal_ecRecover(byte[] message, byte[] signature);
        
        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<byte[]> personal_sign(byte[] message, Address address, string passphrase = null);
    }
}