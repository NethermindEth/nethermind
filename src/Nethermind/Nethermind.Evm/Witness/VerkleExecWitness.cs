// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm.Witness;

public class VerkleExecWitness(ILogManager logManager) : IExecutionWitness
{
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

    private readonly JournalSet<Hash256> _accessedLeaves = new();
    private readonly JournalSet<byte[]> _accessedSubtrees = new(Bytes.EqualityComparer);

    private readonly JournalSet<Hash256> _modifiedLeaves = new();
    private readonly JournalSet<byte[]> _modifiedSubtrees = new(Bytes.EqualityComparer);


    public long AccessForContractCreationInit(Address contractAddress, bool isValueTransfer)
    {
        var gas = AccessVersion(contractAddress, true) + AccessNonce(contractAddress, true);
        if (isValueTransfer) gas += AccessBalance(contractAddress, true);
        // _logger.Info($"AccessForContractCreationInit: {contractAddress.Bytes.ToHexString()} {isValueTransfer} {gas}");

        return gas;    }

    public long AccessForContractCreated(Address contractAddress)
    {
        var gas = AccessCompleteAccount(contractAddress, true);
        // _logger.Info($"AccessContractCreated: {contractAddress.Bytes.ToHexString()} {gas}");
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

    public long AccessForCodeOpCodes(Address caller)
    {
        var gas = AccessVersion(caller);
        gas += AccessCodeSize(caller);
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return gas;
    }

    public long AccessForBalance(Address address, bool isWrite = false)
    {
        return AccessBalance(address, isWrite);
    }

    public long AccessForCodeHash(Address address)
    {
        return AccessCodeHash(address);
    }

    /// <summary>
    ///     When SLOAD and SSTORE opcodes are called with a given address
    ///     and key.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public long AccessForStorage(Address address, UInt256 key, bool isWrite)
    {
        var gas = AccessKey(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), isWrite);
        // _logger.Info($"AccessStorage: {address.Bytes.ToHexString()} {key.ToBigEndian().ToHexString()} {isWrite} {gas}");
        return gas;
    }

    public long AccessForCodeProgramCounter(Address address, int programCounter, bool isWrite)
    {
        return AccessCodeChunk(address, CalculateCodeChunkIdFromPc(programCounter), isWrite);
    }

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite, ref long unspentGas)
    {
        if (startIncluded == endNotIncluded) return true;

        byte startChunkId = CalculateCodeChunkIdFromPc(startIncluded);
        byte endChunkId = CalculateCodeChunkIdFromPc(endNotIncluded - 1);

        long accGas = 0;
        for (byte ch = startChunkId; ch <= endChunkId; ch++)
        {
            long gas = AccessCodeChunk(address, ch, isWrite);
            accGas += gas;
            if (!UpdateGas(gas, ref unspentGas)) return false;
        }
        if (_logger.IsTrace) _logger.Trace($"AccessAndChargeForCodeSlice: {accGas} {startIncluded} {endNotIncluded} {isWrite} {unspentGas}");
        return true;
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


    public long AccessForAbsentAccount(Address address)
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

    public long AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero)
    {
        bool contractNotSameAsBeneficiary = contract != inheritor;
        long gas = 0;
        gas += AccessVersion(contract);
        gas += AccessCodeSize(contract);

        if (balanceIsZero)
        {
            gas += AccessBalance(contract);
            if (contractNotSameAsBeneficiary) gas += AccessBalance(inheritor);
        }
        else
        {
            gas += AccessBalance(contract, true);
            if (contractNotSameAsBeneficiary) gas += AccessBalance(inheritor, true);
        }
        return gas;
    }

    private static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost) return false;
        gasAvailable -= gasCost;
        return true;
    }

    private static byte CalculateCodeChunkIdFromPc(int pc)
    {
        int chunkId = pc / 31;
        return (byte)chunkId;
    }

    public byte[][] GetAccessedKeys()
    {
        return _accessedLeaves.Select(x => x.BytesToArray()).ToArray();
    }

    private long AccessVersion(Address address, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.Version, isWrite);
    }

    private long AccessBalance(Address address, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.Balance, isWrite);
    }

    private long AccessNonce(Address address, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.Nonce, isWrite);
    }

    private long AccessCodeSize(Address address, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.CodeSize, isWrite);
    }

    private long AccessCodeHash(Address address, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.CodeHash, isWrite);
    }

    private long AccessAccountSubTree(Address address, UInt256 treeIndex, byte subIndex, bool isWrite = false)
    {
        return AccessKey(AccountHeader.GetTreeKey(address.Bytes, treeIndex, subIndex), isWrite);
    }

    private long AccessKey(Hash256 key, bool isWrite = false, bool leafExist = false)
    {
        long accessCost = 0;
        if (_accessedLeaves.Add(key)) accessCost += GasCostOf.WitnessChunkRead;
        if (_accessedSubtrees.Add(key.Bytes[..31].ToArray())) accessCost += GasCostOf.WitnessBranchRead;;

        if (isWrite)
        {
            if (_modifiedLeaves.Add(key)) accessCost += GasCostOf.WitnessChunkWrite;
            if (_modifiedSubtrees.Add(key.Bytes[..31].ToArray())) accessCost += GasCostOf.WitnessBranchWrite;
        }
        return accessCost;
    }
}
