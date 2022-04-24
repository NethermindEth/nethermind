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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State;

public class VerkleWitness: IVerkleWitness
{
    // const int VersionLeafKey = 0;
    // const int BalanceLeafKey = 1;
    // const int NonceLeafKey = 2;
    // const int CodeKeccakLeafKey = 3;
    // const int CodeSizeLeafKey = 4;
    private readonly JournalSet<byte[]> _accessedSubtrees;
    private readonly JournalSet<byte[]> _accessedLeaves;
    private readonly JournalSet<byte[]> _modifiedSubtrees;
    private readonly JournalSet<byte[]> _modifiedLeaves;
    
    public const long WitnessChunkRead = 200; // verkle-trie
    public const long WitnessChunkWrite = 500; // verkle-trie
    public const long WitnessChunkFill = 6200; // verkle-trie
    public const long WitnessBranchRead = 1900; // verkle-trie
    public const long WitnessBranchWrite = 3000; // verkle-trie
    
    private readonly Dictionary<int, int[]> _snapshots = new();
    private int NextSnapshot = 0;
    
    public VerkleWitness()
    {
        _accessedSubtrees = new JournalSet<byte[]>();
        _accessedLeaves = new JournalSet<byte[]>();
    }

    public byte[,] GetAccessedKeys()
    {
        return To2D(_accessedLeaves.ToArray());
    }

    public static byte[,] To2D(byte[][] jagged) 
    {
        byte [,] keys = new byte[jagged.Length, 32];
        unsafe
        {
            for (int i = 0; i < jagged.Length; i++)
            {
                fixed(byte * pInKey = jagged[i])
                {
                    fixed(byte * pOutKey = &keys[i, 0])
                    {
                        Buffer.MemoryCopy(pInKey, pOutKey, 32, 32);
                    }
                }
            }
        }

        return keys;
    }

    public int TakeSnapshot()
    {
        int[] snapshot = new int[2];
        snapshot[0] = _accessedSubtrees.TakeSnapshot();
        snapshot[1] = _accessedLeaves.TakeSnapshot();
        _snapshots.Add(NextSnapshot,snapshot);
        return NextSnapshot++;
    }

    public void Restore(int snapshot)
    {
        int[] Snapshot = _snapshots[snapshot]; 
        _accessedSubtrees.Restore(Snapshot[0]);
        _accessedLeaves.Restore(Snapshot[1]);
    }

    public long AccessCodeOpCodes(Address caller)
    {
        bool[] accountAccess = {true, false, false, false, true};
        return AccessAccount(caller, accountAccess);
    }

    public long AccessValueTransfer(Address caller, Address callee)
    {
        bool[] accountAccess = {false, true, false, false, false};
        return AccessAccount(caller, accountAccess) + AccessAccount(callee, accountAccess);
    }
    
    public long AccessContractCreationInit(Address contractAddress)
    {
        bool[] accountAccess = {true, true, true, false, false};
        return AccessAccount(contractAddress, accountAccess);
    }
    
    public long AccessContractCreated(Address contractAddress)
    {
        bool[] accountAccess = {true, true, true, true, true};
        return AccessAccount(contractAddress, accountAccess);
    }
    
    public long AccessBalance(Address address)
    {
        bool[] accountAccess = {false, true, false, false, false};
        return AccessAccount(address, accountAccess);
    }
    
    public long AccessCodeHash(Address address)
    {
        bool[] accountAccess = {false, false, false, true, false};
        return AccessAccount(address, accountAccess);
    }
    
    public long AccessStorage(Address address, byte key)
    {
        (int stemAccess, int chunkAccess) = AccessKey(VerkleUtils.GetTreeKeyForStorageSlot(address, key));
        return stemAccess * WitnessBranchRead + chunkAccess + WitnessChunkRead;
    }

    public long AccessCodeChunk(Address address, byte chunkId)
    {
        (int stemAccess, int chunkAccess) = AccessKey(VerkleUtils.GetTreeKeyForCodeChunk(address, chunkId));
        return stemAccess + WitnessBranchRead + chunkAccess + WitnessChunkRead;
    }
    
    public long AccessCompleteAccount(Address address)
    {
        bool[] accountAccess = {true, true, true, true, true};
        return AccessAccount(address, accountAccess);
    }
    
    public long AccessAccount(Address address, bool[] bitVector)
    {

        long gasUsed = 0;
        for (int i = 0; i < bitVector.Length; i++)
        {
            if (bitVector[i])
            {
                (int stemAccess, int chunkAccess) = AccessKey(VerkleUtils.GetTreeKey(address, UInt256.Zero,(byte) i));
                gasUsed += stemAccess + WitnessBranchRead + chunkAccess + WitnessChunkRead;
            }
        }

        return gasUsed;
    }
    
    public (int, int) AccessKey(byte[] key)
    {
        bool newSubTree = false;
        bool newLeaf = false;
        
        if (!_accessedLeaves.Contains((key)))
        {
            newLeaf = true;
            _accessedLeaves.Add((key));
        }

        if (!_accessedSubtrees.Add(key[..31]))
        {
            newSubTree = true;
            _accessedSubtrees.Add(key[..31]);
        }

        return (newSubTree?1:0, newLeaf?1:0);
    }
    
    
    public long AccessForTransaction(Address originAddress, Address destinationAddress)
    {
        return AccessCompleteAccount(originAddress) + AccessCompleteAccount(destinationAddress);
    }
    
}
