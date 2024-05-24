// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Witness;

public class VerkleExecWitness(ILogManager logManager) : IExecutionWitness
{
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

    private readonly JournalSet<Hash256> _accessedLeaves = new();
    private readonly JournalSet<byte[]> _accessedSubtrees = new(Bytes.EqualityComparer);

    private readonly JournalSet<Hash256> _modifiedLeaves = new();
    private readonly JournalSet<byte[]> _modifiedSubtrees = new(Bytes.EqualityComparer);


    public bool AccessForContractCreationInit(Address contractAddress, ref long gasAvailable, bool isValueTransfer)
    {
        if (!AccessVersion(contractAddress, ref gasAvailable, true)) return false;
        if (!AccessNonce(contractAddress, ref gasAvailable, true)) return false;
        if (isValueTransfer && !AccessBalance(contractAddress, ref gasAvailable, true)) return false;
        // _logger.Info($"AccessForContractCreationInit: {contractAddress.Bytes.ToHexString()} {isValueTransfer} {gas}");

        return true;
    }

    public bool AccessForContractCreated(Address contractAddress, ref long gasAvailable)
    {
        var gas = AccessCompleteAccount(contractAddress, ref gasAvailable, true);
        // _logger.Info($"AccessContractCreated: {contractAddress.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    ///     When you are starting to execute a transaction.
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public bool AccessForTransaction(Address originAddress, Address? destinationAddress, ref long gasAvailable, bool isValueTransfer)
    {
        // TODO: does not seem right - not upto spec
        if (!AccessVersion(originAddress, ref gasAvailable)) return false;
        // when you are executing a transaction, you are writing to the nonce of the origin address
        if (!AccessNonce(originAddress, ref gasAvailable, true)) return false;
        if (!AccessBalance(originAddress, ref gasAvailable, true)) return false;
        if (!AccessCodeHash(originAddress, ref gasAvailable)) return false;
        if (!AccessCodeSize(originAddress, ref gasAvailable)) return false;

        if (destinationAddress is not null)
        {
            if (!AccessVersion(destinationAddress, ref gasAvailable)) return false;
            if (!AccessNonce(destinationAddress, ref gasAvailable)) return false;
            // when you are executing a transaction with value transfer,
            // you are writing to the balance of the origin and destination address
            if (!AccessBalance(destinationAddress, ref gasAvailable, isValueTransfer)) return false;
            if (!AccessCodeHash(destinationAddress, ref gasAvailable)) return false;
            if (!AccessCodeSize(destinationAddress, ref gasAvailable)) return false;

        }
        // _logger.Info($"AccessForTransaction: {originAddress.Bytes.ToHexString()} {destinationAddress?.Bytes.ToHexString()} {isValueTransfer} {gasCost}");
        return true;
    }

    /// <summary>
    ///     Call for the gas beneficiary.
    /// </summary>
    /// <param name="gasBeneficiary"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForGasBeneficiary(Address gasBeneficiary, ref long gasAvailable)
    {
        if (!AccessVersion(gasBeneficiary, ref gasAvailable)) return false;
        if (!AccessNonce(gasBeneficiary, ref gasAvailable)) return false;
        if (!AccessBalance(gasBeneficiary, ref gasAvailable)) return false;
        if (!AccessCodeHash(gasBeneficiary, ref gasAvailable)) return false;
        if (!AccessCodeSize(gasBeneficiary, ref gasAvailable)) return false;
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return true;
    }

    public bool AccessForCodeOpCodes(Address caller, ref long gasAvailable)
    {
        if (!AccessVersion(caller, ref gasAvailable)) return false;
        if (!AccessCodeSize(caller, ref gasAvailable)) return false;
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return true;
    }

    public bool AccessForBalance(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessBalance(address, ref gasAvailable, isWrite);
    }

    public bool AccessForCodeHash(Address address, ref long gasAvailable)
    {
        return AccessCodeHash(address, ref gasAvailable);
    }

    /// <summary>
    ///     When SLOAD and SSTORE opcodes are called with a given address
    ///     and key.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public bool AccessForStorage(Address address, UInt256 key, ref long gasAvailable, bool isWrite)
    {
        if (address.IsPrecompile(Osaka.Instance)) return true;
        var gas = AccessKey(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), ref gasAvailable, isWrite);
        // _logger.Info($"AccessStorage: {address.Bytes.ToHexString()} {key.ToBigEndian().ToHexString()} {isWrite} {gas}");
        return gas;
    }

    public bool AccessForCodeProgramCounter(Address address, int programCounter, ref long gasAvailable, bool isWrite)
    {
        return AccessCodeChunk(address, CalculateCodeChunkIdFromPc(programCounter), ref gasAvailable, isWrite);
    }

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, ref long gasAvailable, bool isWrite)
    {
        if (startIncluded == endNotIncluded) return true;

        UInt256 startChunkId = CalculateCodeChunkIdFromPc(startIncluded);
        UInt256 endChunkId = CalculateCodeChunkIdFromPc(endNotIncluded - 1);

        long gasBefore = gasAvailable;
        for (UInt256 ch = startChunkId; ch <= endChunkId; ch++)
        {
            if (!AccessCodeChunk(address, ch, ref gasAvailable, isWrite)) return false;
        }
        long gasAfter = gasAvailable;
        long accGas = gasBefore - gasAfter;

        if (_logger.IsTrace) _logger.Trace($"AccessAndChargeForCodeSlice: {accGas} {startIncluded} {endNotIncluded} {isWrite} {gasAvailable}");
        return true;
    }

    /// <summary>
    ///     When the code chunk chunk_id is accessed is accessed
    /// </summary>
    /// <param name="address"></param>
    /// <param name="chunkId"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public bool AccessCodeChunk(Address address, UInt256 chunkId, ref long gasAvailable, bool isWrite)
    {
        if (address.IsPrecompile(Osaka.Instance)) return true;
        Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);
        // _logger.Info($"AccessCodeChunkKey: {EnumerableExtensions.ToString(key)}");
        var gas = AccessKey(key, ref gasAvailable, isWrite);
        // _logger.Info($"AccessCodeChunk: {address.Bytes.ToHexString()} {chunkId} {isWrite} {gas}");
        return gas;
    }


    public bool AccessForAbsentAccount(Address address, ref long gasAvailable)
    {
        var gas = AccessCompleteAccount(address, ref gasAvailable);
        // _logger.Info($"AccessForProofOfAbsence: {address.Bytes.ToHexString()} {gas}");
        return gas;
    }

    /// <summary>
    ///     When you have to access the complete account
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    public bool AccessCompleteAccount(Address address, ref long gasAvailable, bool isWrite = false)
    {
        if (!AccessVersion(address, ref gasAvailable, isWrite)) return false;
        if (!AccessNonce(address, ref gasAvailable, isWrite)) return false;
        if (!AccessBalance(address, ref gasAvailable, isWrite)) return false;
        if (!AccessCodeHash(address, ref gasAvailable, isWrite)) return false;
        if (!AccessCodeSize(address, ref gasAvailable, isWrite)) return false;
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return true;
    }

    public bool AccessForSelfDestruct(Address contract, Address inheritor, ref long gasAvailable, bool balanceIsZero, bool inheritorExist)
    {
        bool contractNotSameAsBeneficiary = contract != inheritor;
        if (AccessVersion(contract, ref gasAvailable)) return false;
        if (AccessCodeSize(contract, ref gasAvailable)) return false;

        if (!inheritorExist && !balanceIsZero)
        {
            if (!AccessVersion(inheritor, ref gasAvailable)) return false;
            if (!AccessNonce(inheritor, ref gasAvailable)) return false;
        }
        if (balanceIsZero)
        {
            if (!AccessBalance(contract, ref gasAvailable)) return false;
            if (contractNotSameAsBeneficiary && !AccessBalance(inheritor, ref gasAvailable)) return false;
        }
        else
        {
            if (!AccessBalance(contract, ref gasAvailable, true)) return false;
            if (contractNotSameAsBeneficiary && !AccessBalance(inheritor, ref gasAvailable, true)) return false;
        }
        return true;
    }

    private static UInt256 CalculateCodeChunkIdFromPc(int pc)
    {
        int chunkId = pc / 31;
        return (UInt256)chunkId;
    }

    public byte[][] GetAccessedKeys()
    {
        return _accessedLeaves.Select(x => x.BytesToArray()).ToArray();
    }

    private bool AccessVersion(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.Version, ref gasAvailable, isWrite);
    }

    private bool AccessBalance(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.Balance, ref gasAvailable, isWrite);
    }

    private bool AccessNonce(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.Nonce, ref gasAvailable, isWrite);
    }

    private bool AccessCodeSize(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.CodeSize, ref gasAvailable, isWrite);
    }

    private bool AccessCodeHash(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.CodeHash, ref gasAvailable, isWrite);
    }

    private bool AccessAccountSubTree(Address address, UInt256 treeIndex, byte subIndex, ref long gasAvailable, bool isWrite = false)
    {
        if (address.IsPrecompile(Osaka.Instance)) return true;
        return AccessKey(AccountHeader.GetTreeKey(address.Bytes, treeIndex, subIndex), ref gasAvailable, isWrite);
    }

    private bool AccessKey(Hash256 key, ref long gasAvailable, bool isWrite = false, bool leafExist = false)
    {
        if (!_accessedLeaves.Contains(key))
        {
            if (gasAvailable < GasCostOf.WitnessChunkRead) return false;
            _accessedLeaves.Add(key);
            gasAvailable -= GasCostOf.WitnessChunkRead;
        }

        if (!_accessedSubtrees.Contains(key.Bytes[..31].ToArray()))
        {
            if (gasAvailable < GasCostOf.WitnessBranchRead) return false;
            _accessedSubtrees.Add(key.Bytes[..31].ToArray());
            gasAvailable -= GasCostOf.WitnessBranchRead;
        }

        if (!isWrite) return true;

        if (!_modifiedLeaves.Contains(key))
        {
            if (gasAvailable < GasCostOf.WitnessChunkWrite) return false;
            _modifiedLeaves.Add(key);
            gasAvailable -= GasCostOf.WitnessChunkWrite;
        }

        if (!_modifiedSubtrees.Contains(key.Bytes[..31].ToArray()))
        {
            if (gasAvailable < GasCostOf.WitnessBranchWrite) return false;
            _modifiedSubtrees.Add(key.Bytes[..31].ToArray());
            gasAvailable -= GasCostOf.WitnessBranchWrite;
        }

        return true;
    }
}
