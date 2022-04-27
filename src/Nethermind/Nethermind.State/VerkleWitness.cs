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
        _modifiedLeaves = new JournalSet<byte[]>();
        _modifiedSubtrees = new JournalSet<byte[]>();
    }
    /// <summary>
    /// When a non-precompile address is the target of a CALL, CALLCODE,
    /// DELEGATECALL, SELFDESTRUCT, EXTCODESIZE, or EXTCODECOPY opcode,
    /// or is the target address of a contract creation whose initcode
    /// starts execution.
    /// </summary>
    /// <param name="caller"></param>
    /// <returns></returns>
    public long AccessForCodeOpCodes(Address caller)
    {
        // (address, 0, VERSION_LEAF_KEY)
        // (address, 0, CODE_SIZE_LEAF_KEY)
        bool[] accountAccess = {true, false, false, false, true};
        return AccessAccount(caller, accountAccess);
    }

    /// <summary>
    /// Use this in two scenarios: 
    /// 1. If a call is value-bearing (ie. it transfers nonzero wei), whether
    /// or not the callee is a precompile
    /// 2. If the SELFDESTRUCT/SENDALL opcode is called by some caller_address
    /// targeting some target_address (regardless of whether it’s value-bearing
    /// or not)
    /// </summary>
    /// <param name="caller"></param>
    /// <param name="callee"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessValueTransfer(Address caller, Address callee)
    {
        // (caller_address, 0, BALANCE_LEAF_KEY)
        // (callee_address, 0, BALANCE_LEAF_KEY)
        bool[] accountAccess = {false, true, false, false, false};
        return AccessAccount(caller, accountAccess, true) + AccessAccount(callee, accountAccess, true);
    }
    
    /// <summary>
    /// When a contract creation is initialized.
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer)
    {
        // (contract_address, 0, VERSION_LEAF_KEY)
        // (contract_address, 0, NONCE_LEAF_KEY)
        bool[] accountAccess = {true, false, true, false, false};
        if (isValueTransfer)
        {
            // (contract_address, 0, BALANCE_LEAF_KEY)
            accountAccess[1] = true;
        }
        return AccessAccount(contractAddress, accountAccess, true);
    }
    
    /// <summary>
    /// When a contract is created.
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <returns></returns>
    public long AccessContractCreated(Address contractAddress)
    {
        // (contract_address, 0, VERSION_LEAF_KEY)
        // (contract_address, 0, NONCE_LEAF_KEY)
        // (contract_address, 0, BALANCE_LEAF_KEY)
        // (contract_address, 0, CODE_KECCAK_LEAF_KEY)
        // (contract_address, 0, CODE_SIZE_LEAF_KEY)
        return AccessCompleteAccount(contractAddress, true);
    }
    
    /// <summary>
    /// If the BALANCE opcode is called targeting some address.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public long AccessBalance(Address address)
    {
        // (address, 0, BALANCE_LEAF_KEY)
        bool[] accountAccess = {false, true, false, false, false};
        return AccessAccount(address, accountAccess);
    }
    
    /// <summary>
    /// If the EXTCODEHASH opcode is called targeting some address.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public long AccessCodeHash(Address address)
    {
        
        bool[] accountAccess = {false, false, false, true, false};
        return AccessAccount(address, accountAccess);
    }
    
    /// <summary>
    /// When SLOAD and SSTORE opcodes are called with a given address
    /// and key.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessStorage(Address address, UInt256 key, bool isWrite)
    {
        return AccessKey(VerkleUtils.GetTreeKeyForStorageSlot(address, key), isWrite);
    }
    
    /// <summary>
    /// When the code chunk chunk_id is accessed is accessed 
    /// </summary>
    /// <param name="address"></param>
    /// <param name="chunkId"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessCodeChunk(Address address, byte chunkId, bool isWrite)
    {
        return AccessKey(VerkleUtils.GetTreeKeyForCodeChunk(address, chunkId), isWrite);
    }
    
    /// <summary>
    /// When you are starting to execute a transaction.
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessForTransaction(Address originAddress, Address destinationAddress, bool isValueTransfer)
    {
        
        long gasCost = AccessCompleteAccount(originAddress) + AccessCompleteAccount(destinationAddress);
        
        // when you are executing a transaction, you are writing to the nonce of the origin address
        bool[] accountAccess = {false, false, true, false, false};
        gasCost += AccessAccount(originAddress, accountAccess, true);
        if (isValueTransfer)
        {
            // when you are executing a transaction with value transfer,
            // you are writing to the balance of the origin and destination address
            gasCost += AccessValueTransfer(originAddress, destinationAddress);
        }

        return gasCost;
    }
    
    /// <summary>
    /// When you have to access the complete account
    /// </summary>
    /// <param name="address"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessCompleteAccount(Address address, bool isWrite = false)
    {
        bool[] accountAccess = {true, true, true, true, true};
        return AccessAccount(address, accountAccess, isWrite);
    }
    
    /// <summary>
    /// When you have to access the certain keys for the account
    /// you can specify the keys you want to access using the bitVector.
    /// set the bits to true if you want to access the key.
    /// bitVector[0] for VersionLeafKey
    /// bitVector[1] for BalanceLeafKey
    /// bitVector[2] for NonceLeafKey
    /// bitVector[3] for CodeKeccakLeafKey
    /// bitVector[4] for CodeSizeLeafKey
    /// </summary>
    /// <param name="address"></param>
    /// <param name="bitVector"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessAccount(Address address, bool[] bitVector, bool isWrite=false)
    {

        long gasUsed = 0;
        for (int i = 0; i < bitVector.Length; i++)
        {
            if (bitVector[i])
            {
                gasUsed += AccessKey(VerkleUtils.GetTreeKey(address, UInt256.Zero,(byte) i), isWrite);
            }
        }

        return gasUsed;
    }
    
    public long AccessKey(byte[] key, bool isWrite = false)
    {
        bool newSubTreeAccess = false;
        bool newSubTreeWrite = false;
        bool newLeafAccess = false;
        bool newLeafWrite = false;
        bool newLeafFill = false;
        
        if (!_accessedLeaves.Contains((key)))
        {
            newLeafAccess = true;
            _accessedLeaves.Add((key));
        }

        if (!_accessedSubtrees.Add(key[..31]))
        {
            newSubTreeAccess = true;
            _accessedSubtrees.Add(key[..31]);
        }
        
        if (isWrite)
        {
            if (!_modifiedLeaves.Contains((key)))
            {
                newLeafWrite = true;
                _modifiedLeaves.Add((key));
                // are we just writing or filling the chunk? - implement the difference
            }

            if (!_modifiedSubtrees.Add(key[..31]))
            {
                newSubTreeWrite = true;
                _modifiedSubtrees.Add(key[..31]);
            }
        }

        return (newLeafAccess ? WitnessChunkRead : 0) +
               (newLeafWrite ? WitnessChunkWrite : 0) +
               (newLeafFill ? WitnessChunkFill : 0) +
               (newSubTreeAccess ? WitnessBranchRead : 0) +
               (newSubTreeWrite ? WitnessBranchWrite : 0);
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
    
}
