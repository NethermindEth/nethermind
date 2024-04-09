// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;

namespace Nethermind.Verkle.Tree.Utils;

// TODO: this can be definitely optimized by caching the keys from StateProvider - because for every access we
//       already calculate keys in StateProvider - or we maintain pre images?
public class VerkleWitness: IJournal<int>
{
    private readonly JournalSet<Hash256> _accessedLeaves = new();
    private readonly JournalSet<byte[]> _accessedSubtrees = new(Bytes.EqualityComparer);

    private readonly JournalSet<Hash256> _modifiedLeaves = new();
    private readonly JournalSet<byte[]> _modifiedSubtrees = new(Bytes.EqualityComparer);

    private readonly Dictionary<int, int[]> _snapshots = new();
    private int _nextSnapshot;

    /// <summary>
    ///     When a non-precompile address is the target of a CALL, CALLCODE,
    ///     DELEGATECALL, SELFDESTRUCT, EXTCODESIZE, or EXTCODECOPY opcode,
    ///     or is the target address of a contract creation whose initcode
    ///     starts execution.
    /// </summary>
    /// <param name="caller"></param>
    /// <returns></returns>
    public long AccessForCodeOpCodes(Address caller)
    {
        var gas = AccessVersion(caller);
        gas += AccessCodeSize(caller);
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    ///     Use this in two scenarios:
    ///     1. If a call is value-bearing (ie. it transfers nonzero wei), whether
    ///     or not the callee is a precompile
    ///     2. If the SELFDESTRUCT/SENDALL opcode is called by some caller_address
    ///     targeting some target_address (regardless of whether it’s value-bearing
    ///     or not)
    /// </summary>
    /// <param name="caller"></param>
    /// <param name="callee"></param>
    /// <returns></returns>
    public long AccessValueTransfer(Address caller, Address? callee)
    {
        var gas = AccessBalance(caller, true) + (callee == null ? 0 : AccessBalance(callee, true));
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    ///     When a contract creation is initialized.
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer)
    {
        var gas = AccessVersion(contractAddress, true) + AccessNonce(contractAddress, true);
        if (isValueTransfer) gas += AccessBalance(contractAddress, true);
        // _logger.Info($"AccessForContractCreationInit: {contractAddress.Bytes.ToHexString()} {isValueTransfer} {gas}");

        return gas;
    }

    /// <summary>
    ///     When a contract is created.
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <returns></returns>
    public long AccessContractCreated(Address contractAddress)
    {
        var gas = AccessCompleteAccount(contractAddress, true);
        // _logger.Info($"AccessContractCreated: {contractAddress.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    ///     When SLOAD and SSTORE opcodes are called with a given address
    ///     and key.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessStorage(Address address, UInt256 key, bool isWrite)
    {
        var gas = AccessKey(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), isWrite);
        // _logger.Info($"AccessStorage: {address.Bytes.ToHexString()} {key.ToBigEndian().ToHexString()} {isWrite} {gas}");
        return gas;
    }

    /// <summary>
    ///     When the code chunk chunk_id is accessed is accessed
    /// </summary>
    /// <param name="address"></param>
    /// <param name="chunkId"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessCodeChunk(Address address, byte chunkId, bool isWrite)
    {
        Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);
        // _logger.Info($"AccessCodeChunkKey: {EnumerableExtensions.ToString(key)}");
        var gas = AccessKey(key, isWrite);
        // _logger.Info($"AccessCodeChunk: {address.Bytes.ToHexString()} {chunkId} {isWrite} {gas}");
        return gas;
    }

    /// <summary>
    ///     When you are starting to execute a transaction.
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public long AccessForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer)
    {
        // TODO: does not seem right - not upto spec
        long gasCost = 0;
        gasCost += AccessVersion(originAddress);
        // when you are executing a transaction, you are writing to the nonce of the origin address
        gasCost += AccessNonce(originAddress, true);
        gasCost += AccessBalance(originAddress, true);
        gasCost += AccessCodeHash(originAddress);
        gasCost += AccessCodeSize(originAddress);

        if (destinationAddress is not null)
        {
            gasCost += AccessVersion(destinationAddress);
            gasCost += AccessNonce(destinationAddress);
            // when you are executing a transaction with value transfer,
            // you are writing to the balance of the origin and destination address
            gasCost += AccessBalance(destinationAddress, isValueTransfer);
            gasCost += AccessCodeHash(destinationAddress);
            gasCost += AccessCodeSize(destinationAddress);

        }
        // _logger.Info($"AccessForTransaction: {originAddress.Bytes.ToHexString()} {destinationAddress?.Bytes.ToHexString()} {isValueTransfer} {gasCost}");
        return gasCost;
    }

    /// <summary>
    ///     Proof of Absence
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public long AccessForProofOfAbsence(Address address)
    {
        var gas = AccessCompleteAccount(address);
        // _logger.Info($"AccessForProofOfAbsence: {address.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    ///     When you have to access the complete account
    /// </summary>
    /// <param name="address"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessCompleteAccount(Address address, bool isWrite = false)
    {
        long gasCost = 0;
        gasCost += AccessVersion(address, isWrite);
        gasCost += AccessNonce(address, isWrite);
        gasCost += AccessBalance(address, isWrite);
        gasCost += AccessCodeHash(address, isWrite);
        gasCost += AccessCodeSize(address, isWrite);
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return gasCost;
    }

    /// <summary>
    ///     Call for the gas beneficiary.
    /// </summary>
    /// <param name="gasBeneficiary"></param>
    /// <returns></returns>
    public long AccessForGasBeneficiary(Address gasBeneficiary)
    {
        long gas = 0;
        gas += AccessVersion(gasBeneficiary);
        gas += AccessNonce(gasBeneficiary);
        gas += AccessBalance(gasBeneficiary);
        gas += AccessCodeHash(gasBeneficiary);
        gas += AccessCodeSize(gasBeneficiary);
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return gas;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="contract"></param>
    /// <param name="beneficiary"></param>
    /// <param name="balanceIsZero"></param>
    /// <returns></returns>
    public long AccessForSelfDestruct(Address contract, Address beneficiary, bool balanceIsZero)
    {
        bool contractNotSameAsBeneficiary = contract != beneficiary;
        long gas = 0;
        gas += AccessVersion(contract);
        gas += AccessCodeSize(contract);

        if (contractNotSameAsBeneficiary)
        {
            gas += AccessVersion(beneficiary);
            gas += AccessCodeSize(beneficiary);
        }


        if (balanceIsZero)
        {
            gas += AccessBalance(contract);
            if (contractNotSameAsBeneficiary) gas += AccessBalance(beneficiary);
        }
        else
        {
            gas += AccessBalance(contract, true);
            if (contractNotSameAsBeneficiary) gas += AccessBalance(beneficiary, true);
        }
        return gas;
    }

    public byte[][] GetAccessedKeys()
    {
        return _accessedLeaves.Select(x => x.BytesToArray()).ToArray();
    }

    public int TakeSnapshot()
    {
        var snapshot = new int[2];
        snapshot[0] = _accessedSubtrees.TakeSnapshot();
        snapshot[1] = _accessedLeaves.TakeSnapshot();
        _snapshots.Add(_nextSnapshot, snapshot);
        return _nextSnapshot++;
    }

    public void Restore(int snapshot)
    {
        var witnessSnapshot = _snapshots[snapshot];
        _accessedSubtrees.Restore(witnessSnapshot[0]);
        _accessedLeaves.Restore(witnessSnapshot[1]);
    }

    # region WitnessAccess helper
    private long AccessVersion(Address address, bool isWrite = false)
    {
        return AccessAddress(address, UInt256.Zero, AccountHeader.Version, isWrite);
    }

    public long AccessBalance(Address address, bool isWrite = false)
    {
        return AccessAddress(address, UInt256.Zero, AccountHeader.Balance, isWrite);
    }

    private long AccessNonce(Address address, bool isWrite = false)
    {
        return AccessAddress(address, UInt256.Zero, AccountHeader.Nonce, isWrite);
    }

    private long AccessCodeSize(Address address, bool isWrite = false)
    {
        return AccessAddress(address, UInt256.Zero, AccountHeader.CodeSize, isWrite);
    }

    public long AccessCodeHash(Address address, bool isWrite = false)
    {
        return AccessAddress(address, UInt256.Zero, AccountHeader.CodeHash, isWrite);
    }

    private long AccessAddress(Address address, UInt256 treeIndex, byte subIndex, bool isWrite = false)
    {
        return AccessKey(AccountHeader.GetTreeKey(address.Bytes, treeIndex, subIndex), isWrite);
    }

    private long AccessKey(Hash256 key, bool isWrite = false, bool leafExist = false)
    {
        long accessCost = 0;
        if (_accessedLeaves.Add(key)) accessCost += WitnessChunkRead;
        if (_accessedSubtrees.Add(key.Bytes[..31].ToArray())) accessCost += WitnessBranchRead;;

        if (isWrite)
        {
            if (_modifiedLeaves.Add(key)) accessCost += WitnessChunkWrite;
            if (_modifiedSubtrees.Add(key.Bytes[..31].ToArray())) accessCost += WitnessBranchWrite;
        }
        return accessCost;
    }
    #endregion

    # region GasCost verkle
    // TODO: add these in GasPrices List
    private const long WitnessChunkRead = 200; // verkle-trie
    private const long WitnessChunkWrite = 500; // verkle-trie
    private const long WitnessChunkFill = 6200; // verkle-trie
    private const long WitnessBranchRead = 1900; // verkle-trie
    private const long WitnessBranchWrite = 3000; // verkle-trie
    #endregion
}
