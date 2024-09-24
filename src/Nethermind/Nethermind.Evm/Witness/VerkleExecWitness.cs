// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

[assembly: InternalsVisibleTo("Nethermind.State.Test")]
namespace Nethermind.Evm.Witness;

public class VerkleExecWitness(ILogManager logManager, VerkleWorldState? verkleWorldState) : IExecutionWitness
{
    private readonly HashSet<Hash256> _accessedLeaves = new();
    private readonly HashSet<byte[]> _accessedSubtrees = new(Bytes.EqualityComparer);
    private readonly ILogger _logger = logManager.GetClassLogger();

    private readonly HashSet<Hash256> _modifiedLeaves = new();
    private readonly HashSet<byte[]> _modifiedSubtrees = new(Bytes.EqualityComparer);

    private readonly VerkleWorldState _verkleWorldState =
        verkleWorldState ?? throw new ArgumentNullException(nameof(verkleWorldState));

    private bool ChargeFillCost { get; } = false;


    /// <summary>
    ///     When:
    ///     1. a non-precompile address is the target of a *CALL, SELFDESTRUCT, EXTCODESIZE, or EXTCODECOPY opcode,
    ///     2. a non-precompile address is the target address of a contract creation whose initcode starts execution,
    ///     3. any address is the target of the BALANCE opcode
    ///     4. a deployed contract calls CODECOPY
    /// </summary>
    /// <param name="caller"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessAccountData(Address caller, ref long gasAvailable)
    {
        return AccessBasicData<Gas>(caller, ref gasAvailable);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessCodeHash(Address address, ref long gasAvailable)
    {
        return AccessCodeHash<Gas>(address, ref gasAvailable);
    }


    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForBalanceOpCode(Address address, ref long gasAvailable)
    {
        return AccessBasicData<Gas>(address, ref gasAvailable);
    }


    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="isWrite"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForStorage(Address address, UInt256 key, bool isWrite, ref long gasAvailable)
    {
        return AccessKey<Gas>(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), ref gasAvailable, isWrite);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="key"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForBlockHashOpCode(Address address, UInt256 key, ref long gasAvailable)
    {
        return AccessForStorage(address, key, false, ref gasAvailable);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="programCounter"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForCodeProgramCounter(Address address, int programCounter, ref long gasAvailable)
    {
        return AccessCodeChunk(address, CalculateCodeChunkIdFromPc(programCounter), false, ref gasAvailable);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="startIncluded"></param>
    /// <param name="endNotIncluded"></param>
    /// <param name="isWrite"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessAndChargeForCodeSlice(Address address, int startIncluded, int endNotIncluded, bool isWrite,
        ref long gasAvailable)
    {
        if (startIncluded == endNotIncluded) return true;

        UInt256 startChunkId = CalculateCodeChunkIdFromPc(startIncluded);
        UInt256 endChunkId = CalculateCodeChunkIdFromPc(endNotIncluded - 1);

        for (UInt256 ch = startChunkId; ch <= endChunkId; ch++)
            if (!AccessCodeChunk(address, ch, isWrite, ref gasAvailable))
                return false;
        return true;
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="chunkId"></param>
    /// <param name="isWrite"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessCodeChunk(Address address, UInt256 chunkId, bool isWrite, ref long gasAvailable)
    {
        Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(address.Bytes, chunkId);
        return AccessKey<Gas>(key, ref gasAvailable, isWrite);
    }


    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForAbsentAccount(Address address, ref long gasAvailable)
    {
        return AccessCompleteAccount<Gas>(address, ref gasAvailable);
    }

    /// <summary>
    /// </summary>
    /// <param name="contract"></param>
    /// <param name="inheritor"></param>
    /// <param name="balanceIsZero"></param>
    /// <param name="inheritorExist"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForSelfDestruct(Address contract, Address inheritor, bool balanceIsZero, bool inheritorExist,
        ref long gasAvailable)
    {
        // access the basic data for the contract calling the selfdestruct
        if (!AccessBasicData<Gas>(contract, ref gasAvailable)) return false;

        // TODO: move precompile check to outside
        // if the inheritor is a pre-compile and there is no balance transfer, there is nothing else to do
        if (inheritor.IsPrecompile(Osaka.Instance) && balanceIsZero) return true;

        // now if the contract and inheritor is not the same, then access the inheritor basic data
        // here this is charged because we need gas to check if the inheritor exists or not
        var contractNotSameAsBeneficiary = contract != inheritor;
        if (contractNotSameAsBeneficiary && !AccessBasicData<Gas>(inheritor, ref gasAvailable)) return false;

        // now access for write when the balance is non-zero
        if (!balanceIsZero)
        {
            // TODO: do we even need this here, i dont think there is a case where this is already not in witness
            if (!AccessBasicData<Gas>(contract, ref gasAvailable, true)) return false;
            if (!contractNotSameAsBeneficiary) return true;

            if (inheritorExist)
            {
                if (!AccessBasicData<Gas>(inheritor, ref gasAvailable, true)) return false;
            }
            else
            {
                if (!AccessCompleteAccount<Gas>(inheritor, ref gasAvailable, true)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForContractCreationCheck(Address contractAddress, ref long gasAvailable)
    {
        return AccessCompleteAccount<Gas>(contractAddress, ref gasAvailable);
    }

    /// <summary>
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForContractCreationInit(Address contractAddress, ref long gasAvailable)
    {
        return AccessCompleteAccount<Gas>(contractAddress, ref gasAvailable, true);
    }

    /// <summary>
    /// </summary>
    /// <param name="contractAddress"></param>
    /// <param name="gasAvailable"></param>
    /// <returns></returns>
    public bool AccessForContractCreated(Address contractAddress, ref long gasAvailable)
    {
        return AccessCompleteAccount<Gas>(contractAddress, ref gasAvailable, true);
    }


    public Hash256[] GetAccessedKeys()
    {
        return _accessedLeaves.ToArray();
    }

    public bool AccessForValueTransfer(Address from, Address to, ref long gasAvailable)
    {
        return AccessBasicData<Gas>(from, ref gasAvailable, true) && AccessBasicData<Gas>(to, ref gasAvailable, true);
    }


    /// <summary>
    ///     Call for the gas beneficiary.
    /// </summary>
    /// <param name="gasBeneficiary"></param>
    /// <returns></returns>
    public bool AccessForGasBeneficiary(Address gasBeneficiary)
    {
        long fakeGas = 1_000_000;
        return AccessCompleteAccount<NoGas>(gasBeneficiary, ref fakeGas);
    }

    public bool AccessAccountForWithdrawal(Address address)
    {
        long fakeGas = 1_000_000;
        return AccessCompleteAccount<NoGas>(address, ref fakeGas);
    }

    public bool AccessForBlockhashInsertionWitness(Address address, UInt256 key)
    {
        long fakeGas = 1_000_000;
        AccessCompleteAccount<NoGas>(address, ref fakeGas);
        AccessKey<NoGas>(AccountHeader.GetTreeKeyForStorageSlot(address.Bytes, key), ref fakeGas, true);
        return true;
    }

    /// <summary>
    /// </summary>
    /// <param name="originAddress"></param>
    /// <param name="destinationAddress"></param>
    /// <param name="isValueTransfer"></param>
    /// <returns></returns>
    public bool AccessForTransaction(Address originAddress, Address? destinationAddress, bool isValueTransfer)
    {
        long fakeGas = 1_000_000;
        if (!AccessBasicData<NoGas>(originAddress, ref fakeGas, true)) return false;
        if (!AccessCodeHash<NoGas>(originAddress, ref fakeGas)) return false;

        if (destinationAddress is null) return true;

        return AccessBasicData<NoGas>(destinationAddress, ref fakeGas, isValueTransfer) &&
               AccessCodeHash<NoGas>(destinationAddress, ref fakeGas);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <typeparam name="TGasCharge"></typeparam>
    /// <returns></returns>
    private bool AccessBasicData<TGasCharge>(Address address, ref long gasAvailable, bool isWrite = false)
        where TGasCharge : struct, IGasCharge
    {
        return AccessAccountSubTree<TGasCharge>(address, UInt256.Zero, AccountHeader.BasicDataLeafKey, ref gasAvailable,
            isWrite);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <typeparam name="TGasCharge"></typeparam>
    /// <returns></returns>
    private bool AccessCodeHash<TGasCharge>(Address address, ref long gasAvailable, bool isWrite = false)
        where TGasCharge : struct, IGasCharge
    {
        return AccessAccountSubTree<TGasCharge>(address, UInt256.Zero, AccountHeader.CodeHash, ref gasAvailable,
            isWrite);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <returns></returns>
    internal bool AccessCompleteAccount<TGasCharge>(Address address, ref long gasAvailable, bool isWrite = false)
        where TGasCharge : struct, IGasCharge
    {
        return AccessBasicData<TGasCharge>(address, ref gasAvailable, isWrite) &&
               AccessCodeHash<TGasCharge>(address, ref gasAvailable, isWrite);
    }

    /// <summary>
    /// </summary>
    /// <param name="address"></param>
    /// <param name="treeIndex"></param>
    /// <param name="subIndex"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <typeparam name="TGasCharge"></typeparam>
    /// <returns></returns>
    private bool AccessAccountSubTree<TGasCharge>(Address address, UInt256 treeIndex, byte subIndex,
        ref long gasAvailable, bool isWrite = false)
        where TGasCharge : struct, IGasCharge
    {
        return AccessKey<TGasCharge>(AccountHeader.GetTreeKey(address.Bytes, treeIndex, subIndex), ref gasAvailable,
            isWrite);
    }

    /// <summary>
    /// </summary>
    /// <param name="key"></param>
    /// <param name="gasAvailable"></param>
    /// <param name="isWrite"></param>
    /// <typeparam name="TGasCharge"></typeparam>
    /// <returns></returns>
    private bool AccessKey<TGasCharge>(Hash256 key, ref long gasAvailable, bool isWrite = false)
        where TGasCharge : struct, IGasCharge
    {
        // TODO: do we need a SpanHashSet so that we can at least use the span to do the `Contains` check?
        var subTreeStem = key.Bytes[..31].ToArray();
        long requiredGas = 0;
        var wasPreviouslyNotAccessed = false;

        // read check
        if (TGasCharge.ChargeGas)
        {
            wasPreviouslyNotAccessed = !_accessedLeaves.Contains(key);
            if (wasPreviouslyNotAccessed)
            {
                requiredGas += GasCostOf.WitnessChunkRead;
                // if the key is already in `_accessedLeaves`, then checking `_accessedSubtrees` will be redundant
                if (!_accessedSubtrees.Contains(subTreeStem)) requiredGas += GasCostOf.WitnessBranchRead;
            }

            if (requiredGas > gasAvailable) return false;
            gasAvailable -= requiredGas;
        }

        _accessedLeaves.Add(key);
        _accessedSubtrees.Add(subTreeStem);

        if (!isWrite) return true;


        // write check
        if (TGasCharge.ChargeGas)
        {
            requiredGas = 0;
            // if `wasPreviouslyNotAccessed = true`, this implies that _modifiedLeaves.Contains(key) = false
            if (wasPreviouslyNotAccessed || !_modifiedLeaves.Contains(key))
            {
                requiredGas += GasCostOf.WitnessChunkWrite;
                // if key is already in `_modifiedLeaves`, then we should not check if key is present in the tree
                if (ChargeFillCost && !_verkleWorldState.ValuePresentInTree(key))
                    requiredGas += GasCostOf.WitnessChunkFill;

                // if key is already in `_modifiedLeaves`, then checking `_modifiedSubtrees` will be redundant
                if (!_modifiedSubtrees.Contains(subTreeStem)) requiredGas += GasCostOf.WitnessBranchWrite;
            }

            if (requiredGas > gasAvailable) return false;
            gasAvailable -= requiredGas;
        }

        _modifiedLeaves.Add(key);
        _modifiedSubtrees.Add(subTreeStem);

        return true;
    }

    private static UInt256 CalculateCodeChunkIdFromPc(int pc)
    {
        var chunkId = pc / 31;
        return (UInt256)chunkId;
    }

    internal interface IGasCharge
    {
        public static abstract bool ChargeGas { get; }
    }

    internal readonly struct Gas : IGasCharge
    {
        public static bool ChargeGas => true;
    }

    internal readonly struct NoGas : IGasCharge
    {
        public static bool ChargeGas => true;
    }
}
