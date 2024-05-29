// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Witness;

public class VerkleExecWitness(ILogManager logManager, VerkleWorldState? verkleWorldState) : IExecutionWitness
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    private readonly HashSet<Hash256> _accessedLeaves = new();
    private readonly HashSet<byte[]> _accessedSubtrees = new(Bytes.EqualityComparer);

    private readonly HashSet<Hash256> _modifiedLeaves = new();
    private readonly HashSet<byte[]> _modifiedSubtrees = new(Bytes.EqualityComparer);

    private readonly VerkleWorldState _verkleWorldState =
        verkleWorldState ?? throw new ArgumentNullException(nameof(verkleWorldState));

    public bool AccessForContractCreationInit(Address contractAddress, bool isValueTransfer, ref long gasAvailable)
    {
        if (!AccessBasicData(contractAddress, ref gasAvailable, true)) return false;
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
    /// <param name="isValueTransfer"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer, ref long gasAvailable)
    {
        // TODO: does not seem right - not upto spec
        if (!AccessBasicData(originAddress, ref gasAvailable)) return false;
        // when you are executing a transaction, you are writing to the nonce of the origin address
        if (!AccessCodeHash(originAddress, ref gasAvailable)) return false;

        if (destinationAddress is not null)
        {
            // when you are executing a transaction with value transfer,
            // you are writing to the balance of the origin and destination address
            if (!AccessBasicData(destinationAddress, ref gasAvailable, isValueTransfer)) return false;
            if (!AccessCodeHash(destinationAddress, ref gasAvailable)) return false;
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
        if (!AccessBasicData(gasBeneficiary, ref gasAvailable)) return false;
        if (!AccessCodeHash(gasBeneficiary, ref gasAvailable)) return false;
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return true;
    }

    public bool AccessForCodeOpCodes(Address caller, ref long gasAvailable)
    {
        if (!AccessBasicData(caller, ref gasAvailable)) return false;
        // _logger.Info($"AccessForCodeOpCodes: {caller.Bytes.ToHexString()} {gas}");
        return true;
    }

    public bool AccessForBalance(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessBasicData(address, ref gasAvailable, isWrite);
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
    /// <param name="isWrite"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForStorage(Address address, UInt256 key, bool isWrite, ref long gasAvailable)
    {
        if (address.IsPrecompile(Osaka.Instance)) return true;
        var gas = AccessKey(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), ref gasAvailable, isWrite);
        // _logger.Info($"AccessStorage: {address.Bytes.ToHexString()} {key.ToBigEndian().ToHexString()} {isWrite} {gas}");
        return gas;
    }

    public bool AccessForCodeProgramCounter(Address address, int programCounter, bool isWrite, ref long gasAvailable)
    {
        return AccessCodeChunk(address, CalculateCodeChunkIdFromPc(programCounter), isWrite, ref gasAvailable);
    }

    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite, ref long gasAvailable)
    {
        if (startIncluded == endNotIncluded) return true;

        UInt256 startChunkId = CalculateCodeChunkIdFromPc(startIncluded);
        UInt256 endChunkId = CalculateCodeChunkIdFromPc(endNotIncluded - 1);

        long gasBefore = gasAvailable;
        for (UInt256 ch = startChunkId; ch <= endChunkId; ch++)
        {
            if (!AccessCodeChunk(address, ch, isWrite, ref gasAvailable)) return false;
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
    /// <param name="isWrite"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessCodeChunk(Address address, UInt256 chunkId, bool isWrite, ref long gasAvailable)
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
        if (!AccessBasicData(address, ref gasAvailable, isWrite)) return false;
        if (!AccessCodeHash(address, ref gasAvailable, isWrite)) return false;
        // _logger.Info($"AccessCompleteAccount: {address.Bytes.ToHexString()} {isWrite} {gas}");
        return true;
    }

    public bool AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero, bool inheritorExist, ref long gasAvailable)
    {
        bool contractNotSameAsBeneficiary = contract != inheritor;
        if (!AccessBasicData(contract, ref gasAvailable)) return false;

        if (!inheritorExist && !balanceIsZero)
        {
            if (!AccessBasicData(inheritor, ref gasAvailable)) return false;
        }
        if (!balanceIsZero)
        {
            if (!AccessBasicData(contract, ref gasAvailable, true)) return false;
            if (contractNotSameAsBeneficiary && !AccessBasicData(inheritor, ref gasAvailable, true)) return false;
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

    private bool AccessBasicData(Address address, ref long gasAvailable, bool isWrite = false)
    {
        return AccessAccountSubTree(address, UInt256.Zero, AccountHeader.BasicDataLeafKey, ref gasAvailable, isWrite);
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

    private bool AccessKey(Hash256 key, ref long gasAvailable, bool isWrite = false)
    {
        long requiredGas = 0;
        // TODO: do we need a SpanHashSet so that we can at least use the span to do the `Contains` check?
        byte[] subTreeStem = key.Bytes[..31].ToArray();
        bool wasPreviouslyNotAccessed = !_accessedLeaves.Contains(key);
        if (wasPreviouslyNotAccessed)
        {
            requiredGas += GasCostOf.WitnessChunkRead;
            // if key is already in `_accessedLeaves`, then checking `_accessedSubtrees` will be redundant
            if (!_accessedSubtrees.Contains(subTreeStem)) requiredGas += GasCostOf.WitnessBranchRead;
        }

        if (requiredGas > gasAvailable) return false;
        gasAvailable -= requiredGas;

        _accessedLeaves.Add(key);
        _accessedSubtrees.Add(subTreeStem);

        if (!isWrite) return true;


        requiredGas = 0;
        // if `wasPreviouslyNotAccessed = true`, this implies that _modifiedLeaves.Contains(key) = false
        if (wasPreviouslyNotAccessed || !_modifiedLeaves.Contains(key))
        {
            requiredGas += GasCostOf.WitnessChunkWrite;
            // if key is already in `_modifiedLeaves`, then we should not check if key is present in the tree
            if (!_verkleWorldState.ValuePresentInTree(key)) requiredGas += GasCostOf.WitnessChunkFill;

            // if key is already in `_modifiedLeaves`, then checking `_modifiedSubtrees` will be redundant
            if (!_modifiedSubtrees.Contains(subTreeStem)) requiredGas += GasCostOf.WitnessBranchWrite;
        }

        if (requiredGas > gasAvailable) return false;
        gasAvailable -= requiredGas;

        _modifiedLeaves.Add(key);
        _modifiedSubtrees.Add(subTreeStem);

        return true;
    }
}
