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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Consensus.Clique
{
    [RpcModule(ModuleType.Clique)]
    public interface ICliqueModule : IModule
    {
        ResultWrapper<Snapshot> clique_getSnapshot();
        ResultWrapper<Snapshot> clique_getSnapshotAtHash(Keccak hash);
        ResultWrapper<Address[]> clique_getSigners();
        ResultWrapper<Address[]> clique_getSignersAtHash(Keccak hash);
        ResultWrapper<Address[]> clique_getSignersAtNumber(long number);
        ResultWrapper<string[]> clique_getSignersAnnotated();
        ResultWrapper<string[]> clique_getSignersAtHashAnnotated(Keccak hash);
        ResultWrapper<bool> clique_propose(Address signer, bool vote);
        ResultWrapper<bool> clique_discard(Address signer);
        ResultWrapper<bool> clique_produceBlock(Keccak parentHash);
    }
}