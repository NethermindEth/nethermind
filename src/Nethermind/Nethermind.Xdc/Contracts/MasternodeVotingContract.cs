// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Xdc.Contracts;

internal class MasternodeVotingContract(
    Address contractAddress,
    IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory) : Contract(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress))), IMasternodeVotingContract
{
    internal static readonly ContractFunction<Address, UInt256> GetCandidateCapFunction = new AbiSignature("getCandidateCap").With(AbiType.Address).Returns(AbiType.UInt256);
    internal static readonly ContractFunction<Address, Address> GetCandidateOwnerFunction = new AbiSignature("getCandidateOwner").With(AbiType.Address).Returns(AbiType.Address);
    internal static readonly ContractFunction<ValueTuple, Address[]> GetCandidatesFunction = new AbiSignature("getCandidates").With().Returns(AbiType.Array(AbiType.Address));

    private readonly IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;

    public UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate)
    {
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        IConstantContract constant = GetConstant(source);
        return constant.Call(blockHeader, GetCandidateCapFunction, Address.SystemUser, candidate);
    }

    public UInt256 GetCandidateStake(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, GetCandidateCapFunction.Signature.Name, GenerateTransaction<Transaction>(AbiCodec.Encode(GetCandidateCapFunction.Signature, candidate), Address.SystemUser), true);
        return DecodeReturnData(GetCandidateCapFunction, result);
    }

    public Address GetCandidateOwner(BlockHeader blockHeader, Address candidate)
    {
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        IConstantContract constant = GetConstant(source);
        return constant.Call(blockHeader, GetCandidateOwnerFunction, Address.SystemUser, candidate);
    }

    public Address GetCandidateOwner(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, GetCandidateOwnerFunction.Signature.Name, GenerateTransaction<Transaction>(AbiCodec.Encode(GetCandidateOwnerFunction.Signature, candidate), Address.SystemUser), true);
        if (result.Length != 32)
            throw new InvalidOperationException("Expected 'getCandidateOwner' to return exactly one result.");
        return new Address(result.AsSpan().Slice(32 - Address.Size));
    }


    public Address GetCandidateOwner(IWorldState worldState, Address candidate) =>
        ReadAddress(worldState, ValidatorsStateSlot(candidate));

    [SkipLocalsInit]
    public Address[] GetVoters(IWorldState worldState, Address candidate)
    {
        // mapping(address => address[]) voters: the length sits at the mapping slot, the entries at keccak of it.
        UInt256 arraySlot = MappingSlot(candidate, (UInt256)(byte)CandidateContractSlots.Voters);
        UInt256 length = ReadSlot(worldState, arraySlot);
        if (length.IsZero)
        {
            return [];
        }

        EvmWord arraySlotWord = arraySlot.ToBigEndianWord();
        ReadOnlySpan<byte> arraySlotBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref arraySlotWord, 1));
        UInt256 entrySlot = new(ValueKeccak.Compute(arraySlotBytes).Bytes, isBigEndian: true);

        Address[] voters = new Address[(ulong)length];
        for (int i = 0; i < voters.Length; i++)
        {
            voters[i] = ReadAddress(worldState, entrySlot);
            entrySlot += UInt256.One;
        }

        return voters;
    }

    public UInt256 GetVoterStake(IWorldState worldState, Address candidate, Address voter) =>
        // validatorsState[candidate].voters is the struct's third field, hence the +2 before the inner mapping.
        ReadSlot(worldState, MappingSlot(voter, ValidatorsStateSlot(candidate) + 2));

    private static UInt256 ValidatorsStateSlot(Address candidate) =>
        MappingSlot(candidate, (UInt256)(byte)CandidateContractSlots.ValidatorsState);

    /// <summary>Locates <c>mapping[key]</c> for a mapping rooted at <paramref name="mappingSlot"/>.</summary>
    private static UInt256 MappingSlot(Address key, in UInt256 mappingSlot)
    {
        Span<byte> input = stackalloc byte[64];
        input.Clear();
        key.Bytes.CopyTo(input.Slice(12, Address.Size));
        mappingSlot.ToBigEndian(input.Slice(32));
        return new UInt256(ValueKeccak.Compute(input).Bytes, isBigEndian: true);
    }

    private UInt256 ReadSlot(IWorldState worldState, in UInt256 slot)
    {
        worldState.Get(new StorageCell(ContractAddress!, slot), out UInt256 value);
        return value;
    }

    [SkipLocalsInit]
    private Address ReadAddress(IWorldState worldState, in UInt256 slot)
    {
        worldState.Get(new StorageCell(ContractAddress!, slot), out UInt256 value);

        EvmWord word = value.ToBigEndianWord();
        ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref word, 1));
        return new Address(raw.Slice(32 - Address.Size));
    }

    public Address[] GetCandidates(BlockHeader blockHeader)
    {
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        IConstantContract constant = GetConstant(source);
        return constant.Call(blockHeader, GetCandidatesFunction, Address.SystemUser, default);
    }

    public Address[] GetCandidates(ITransactionProcessor transactionProcessor, BlockHeader blockHeader)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, GetCandidatesFunction.Signature.Name, GenerateTransaction<Transaction>(AbiCodec.Encode(GetCandidatesFunction.Signature, default), Address.SystemUser), true);
        return DecodeReturnData(GetCandidatesFunction, result);
    }

    /// <summary>
    /// Returns an array of masternode candidates sorted by stake
    /// </summary>
    /// <param name="blockHeader"></param>
    /// <returns></returns>
    public Address[] GetCandidatesByStake(BlockHeader blockHeader)
    {
        Address[] candidates = GetCandidates(blockHeader);

        using ArrayPoolList<CandidateStake> candidatesAndStake = new(candidates.Length);
        foreach (Address candidate in candidates)
        {
            if (candidate == Address.Zero)
                continue;

            candidatesAndStake.Add(new CandidateStake()
            {
                Address = candidate,
                Stake = GetCandidateStake(blockHeader, candidate)
            });
        }
        XdcSort.Slice(candidatesAndStake, (x, y) => x.Stake.CompareTo(y.Stake) >= 0);

        Address[] sortedCandidates = new Address[candidatesAndStake.Count];
        for (int i = 0; i < candidatesAndStake.Count; i++)
        {
            sortedCandidates[i] = candidatesAndStake[i].Address;
        }
        return sortedCandidates;
    }

    private enum CandidateContractSlots : byte
    {
        WithdrawsState,
        ValidatorsState,
        Voters,
        KYCString,
        InvalidKYCCount,
        HasVotedInvalid,
        OwnerToCandidate,
        Owners,
        Candidates,
        CandidateCount,
        OwnerCount,
        MinCandidateCap,
        MinVoterCap,
        MaxValidatorNumber,
        CandidateWithdrawDelay,
        VoterWithdrawDelay
    }
}
