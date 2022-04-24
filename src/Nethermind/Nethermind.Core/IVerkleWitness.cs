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

namespace Nethermind.Core;


public interface IVerkleWitness: IJournal<int>
{

    public byte[,] GetAccessedKeys();
    public long AccessCodeOpCodes(Address caller);
    
    public long AccessValueTransfer(Address caller, Address callee);

    public long AccessContractCreationInit(Address contractAddress);
    
    public long AccessContractCreated(Address contractAddress);
   
    public long AccessBalance(Address address);

    public long AccessCodeHash(Address address);

    public long AccessStorage(Address address, byte key);

    public long AccessCodeChunk(Address address, byte chunkId);

    public long AccessCompleteAccount(Address address);

    public long AccessAccount(Address address, bool[] bitVector);

    public (int, int) AccessKey(byte[] key);
    
    public long AccessForTransaction(Address originAddress, Address destinationAddress);

}
