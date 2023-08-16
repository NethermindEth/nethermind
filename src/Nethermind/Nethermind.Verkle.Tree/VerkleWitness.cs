// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;

namespace Nethermind.Verkle.Tree;

// TODO: this can be definitely optimized by caching the keys from StateProvider - because for every access we
//       already calculate keys in StateProvider - or we maintain pre images?
public class VerkleWitness : IVerkleWitness
{
    [Flags]
    private enum AccountHeaderAccess
    {
        Version = 1,
        Balance = 2,
        Nonce = 4,
        CodeHash = 8,
        CodeSize = 16
    }

    private readonly JournalSet<byte[]> _accessedSubtrees;
    private readonly JournalSet<Pedersen> _accessedLeaves;
    private readonly JournalSet<byte[]> _modifiedSubtrees;
    private readonly JournalSet<Pedersen> _modifiedLeaves;

    // TODO: add these in GasPrices List
    private const long WitnessChunkRead = 200; // verkle-trie
    private const long WitnessChunkWrite = 500; // verkle-trie
    private const long WitnessChunkFill = 6200; // verkle-trie
    private const long WitnessBranchRead = 1900; // verkle-trie
    private const long WitnessBranchWrite = 3000; // verkle-trie

    private readonly Dictionary<int, int[]> _snapshots = new Dictionary<int, int[]>();
    private int NextSnapshot;

    public VerkleWitness()
    {
        _accessedSubtrees = new JournalSet<byte[]>(Bytes.EqualityComparer);
        _accessedLeaves = new JournalSet<Pedersen>();
        _modifiedLeaves = new JournalSet<Pedersen>();
        _modifiedSubtrees = new JournalSet<byte[]>(Bytes.EqualityComparer);
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
        long gas = AccessAccount(caller, AccountHeaderAccess.Version | AccountHeaderAccess.CodeSize);
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return gas;
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
    /// <returns></returns>
    public long AccessValueTransfer(Address caller, Address? callee)
    {

        var gas = AccessAccount(caller, AccountHeaderAccess.Balance, true) +
                  // this generally happens in the case of contract creation
                  (callee == null ? 0 : AccessAccount(callee, AccountHeaderAccess.Balance, true));
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    /// When a contract creation is initialized.
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer)
    {
        long gas = isValueTransfer
            ? AccessAccount(contractAddress, AccountHeaderAccess.Version | AccountHeaderAccess.Nonce | AccountHeaderAccess.Balance | AccountHeaderAccess.CodeHash, true)
            : AccessAccount(contractAddress, AccountHeaderAccess.Version | AccountHeaderAccess.Nonce | AccountHeaderAccess.CodeHash, true);
        // _logger.Info($"AccessForContractCreationInit: {contractAddress.Bytes.ToHexString()} {isValueTransfer} {gas}");

        return gas;
    }

    /// <summary>
    /// When a contract is created.
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
    /// If the BALANCE opcode is called targeting some address.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public long AccessBalance(Address address)
    {

        var gas = AccessAccount(address, AccountHeaderAccess.Balance);
        // _logger.Info($"AccessBalance: {address.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    /// If the EXTCODEHASH opcode is called targeting some address.
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public long AccessCodeHash(Address address)
    {

        var gas = AccessAccount(address, AccountHeaderAccess.CodeHash);
        // _logger.Info($"AccessCodeHash: {address.Bytes.ToHexString()} {gas}");
        return gas;
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
        var gas = AccessKey(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), isWrite);
        // _logger.Info($"AccessStorage: {address.Bytes.ToHexString()} {key.ToBigEndian().ToHexString()} {isWrite} {gas}");
        return gas;
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
        var key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);
        // _logger.Info($"AccessCodeChunkKey: {EnumerableExtensions.ToString(key)}");
        var gas = AccessKey(key, isWrite);
        // _logger.Info($"AccessCodeChunk: {address.Bytes.ToHexString()} {chunkId} {isWrite} {gas}");
        return gas;
    }

    /// <summary>
    /// When you are starting to execute a transaction.
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public long AccessForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer)
    {

        // TODO: does not seem right - not upto spec
        long gasCost = AccessAccount(originAddress, AccountHeaderAccess.Version | AccountHeaderAccess.Balance | AccountHeaderAccess.Nonce | AccountHeaderAccess.CodeHash | AccountHeaderAccess.CodeSize)
                       + (destinationAddress == null ? 0 : AccessCompleteAccount(destinationAddress));

        // when you are executing a transaction, you are writing to the nonce of the origin address
        gasCost += AccessAccount(originAddress, AccountHeaderAccess.Nonce, true);
        if (isValueTransfer)
        {
            // when you are executing a transaction with value transfer,
            // you are writing to the balance of the origin and destination address
            gasCost += AccessValueTransfer(originAddress, destinationAddress);
        }
        else
        {
            gasCost += AccessAccount(originAddress, AccountHeaderAccess.Balance, true);
        }
        // _logger.Info($"AccessForTransaction: {originAddress.Bytes.ToHexString()} {destinationAddress?.Bytes.ToHexString()} {isValueTransfer} {gasCost}");
        return gasCost;
    }

    /// <summary>
    /// Proof of Absence
    /// </summary>
    /// <param name="address"></param>
    /// <returns></returns>
    public long AccessForProofOfAbsence(Address address)
    {
        long gas = AccessCompleteAccount(address);
        // _logger.Info($"AccessForProofOfAbsence: {address.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    /// Call for the gas beneficiary.
    /// </summary>
    /// <param name="gasBeneficiary"></param>
    /// <returns></returns>
    public long AccessForGasBeneficiary(Address gasBeneficiary)
    {
        long gas = AccessAccount(gasBeneficiary,
            AccountHeaderAccess.Version | AccountHeaderAccess.Balance | AccountHeaderAccess.Nonce |
            AccountHeaderAccess.CodeHash | AccountHeaderAccess.CodeSize);
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return gas;
    }

    /// <summary>
    /// When you have to access the complete account
    /// </summary>
    /// <param name="address"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessCompleteAccount(Address address, bool isWrite = false)
    {

        var gas = AccessAccount(address,
            AccountHeaderAccess.Version | AccountHeaderAccess.Balance | AccountHeaderAccess.Nonce | AccountHeaderAccess.CodeHash | AccountHeaderAccess.CodeSize,
            isWrite);
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return gas;
    }

    /// <summary>
    /// When you have to access the certain keys for the account
    /// you can specify the keys you want to access using the AccountHeaderAccess.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="accessOptions"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    private long AccessAccount(Address address, AccountHeaderAccess accessOptions, bool isWrite = false)
    {

        long gasUsed = 0;
        if ((accessOptions & AccountHeaderAccess.Version) == AccountHeaderAccess.Version) gasUsed += AccessKey(AccountHeader.GetTreeKey(address.Bytes, UInt256.Zero, AccountHeader.Version), isWrite);
        if ((accessOptions & AccountHeaderAccess.Balance) == AccountHeaderAccess.Balance) gasUsed += AccessKey(AccountHeader.GetTreeKey(address.Bytes, UInt256.Zero, AccountHeader.Balance), isWrite);
        if ((accessOptions & AccountHeaderAccess.Nonce) == AccountHeaderAccess.Nonce) gasUsed += AccessKey(AccountHeader.GetTreeKey(address.Bytes, UInt256.Zero, AccountHeader.Nonce), isWrite);
        if ((accessOptions & AccountHeaderAccess.CodeHash) == AccountHeaderAccess.CodeHash) gasUsed += AccessKey(AccountHeader.GetTreeKey(address.Bytes, UInt256.Zero, AccountHeader.CodeHash), isWrite);
        if ((accessOptions & AccountHeaderAccess.CodeSize) == AccountHeaderAccess.CodeSize) gasUsed += AccessKey(AccountHeader.GetTreeKey(address.Bytes, UInt256.Zero, AccountHeader.CodeSize), isWrite);
        // _logger.Info($"AccessAccount: {address.Bytes.ToHexString()} {accessOptions} {isWrite} {gasUsed}");
        return gasUsed;
    }

    private long AccessKey(Pedersen key, bool isWrite = false, bool leafExist = false)
    {
        bool newSubTreeAccess = false;
        bool newLeafAccess = false;

        bool newSubTreeUpdate = false;
        bool newLeafUpdate = false;

        bool newLeafFill = false;


        if (_accessedLeaves.Add(key))
        {
            newLeafAccess = true;
        }

        if (_accessedSubtrees.Add(key.StemAsSpan.ToArray()))
        {
            newSubTreeAccess = true;
        }

        long accessCost =
            (newLeafAccess ? WitnessChunkRead : 0) +
            (newSubTreeAccess ? WitnessBranchRead : 0);
        if (!isWrite)
            return accessCost;

        if (_modifiedLeaves.Add((key)))
        {
            // newLeafFill = !leafExist;
            newLeafUpdate = true;
        }

        if (_modifiedSubtrees.Add(key.StemAsSpan.ToArray()))
        {
            newSubTreeUpdate = true;
        }
        long writeCost =
            (newLeafUpdate ? WitnessChunkWrite : 0) +
            (newLeafFill ? WitnessChunkFill : 0) +
            (newSubTreeUpdate ? WitnessBranchWrite : 0);

        return writeCost + accessCost;
    }

    public byte[][] GetAccessedKeys()
    {
        return _accessedLeaves.ToArray().Select(x => x.Bytes).ToArray();
    }

    public int TakeSnapshot()
    {
        int[] snapshot = new int[2];
        snapshot[0] = _accessedSubtrees.TakeSnapshot();
        snapshot[1] = _accessedLeaves.TakeSnapshot();
        _snapshots.Add(NextSnapshot, snapshot);
        return NextSnapshot++;
    }

    public void Restore(int snapshot)
    {
        int[] witnessSnapshot = _snapshots[snapshot];
        _accessedSubtrees.Restore(witnessSnapshot[0]);
        _accessedLeaves.Restore(witnessSnapshot[1]);
    }
}
