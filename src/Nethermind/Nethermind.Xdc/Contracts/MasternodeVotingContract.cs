// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using System;

namespace Nethermind.Xdc.Contracts;

internal class MasternodeVotingContract(
    IAbiEncoder abiEncoder,
    Address contractAddress,
    IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory) : Contract(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), CreateAbiDefinition()), IMasternodeVotingContract
{
    private readonly IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;

    private static AbiDefinition CreateAbiDefinition()
    {
        AbiDefinitionParser abiDefinitionParser = new();
        return abiDefinitionParser.Parse(typeof(MasternodeVotingContract));
    }

    public UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate)
    {
        CallInfo callInfo = new(blockHeader, "getCandidateCap", Address.SystemUser, candidate);
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        IConstantContract constant = GetConstant(source);
        object[] result = constant.Call(callInfo);
        if (result.Length != 1)
            throw new InvalidOperationException("Expected 'getCandidateCap' to return exactly one result.");

        return (UInt256)result[0]!;
    }

    public UInt256 GetCandidateStake(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, "getCandidateCap", GenerateTransaction<Transaction>(ContractAddress, "getCandidateCap", Address.SystemUser, candidate), true);
        object[] decoded = DecodeReturnData("getCandidateCap", result);
        if (decoded.Length != 1)
            throw new InvalidOperationException("Expected 'getCandidateCap' to return exactly one result.");

        return (UInt256)decoded[0]!;
    }

    public Address GetCandidateOwner(BlockHeader blockHeader, Address candidate)
    {
        CallInfo callInfo = new(blockHeader, "getCandidateOwner", Address.SystemUser, candidate);
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        IConstantContract constant = GetConstant(source);
        object[] result = constant.Call(callInfo);
        if (result.Length != 1)
            throw new InvalidOperationException("Expected 'getCandidateOwner' to return exactly one result.");

        return (Address)result[0]!;
    }

    public Address GetCandidateOwner(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, "getCandidateOwner", GenerateTransaction<Transaction>(ContractAddress, "getCandidateOwner", Address.SystemUser, candidate), true);
        if (result.Length != 32)
            throw new InvalidOperationException("Expected 'getCandidateOwner' to return exactly one result.");
        return new Address(result.AsSpan().Slice(32 - Address.Size));
    }


    public Address GetCandidateOwner(IWorldState worldState, Address candidate)
    {
        const int ValidatorsStateSlot = (byte)CandidateContractSlots.ValidatorsState;
        Span<byte> mappingKeyInput = stackalloc byte[64];
        mappingKeyInput.Clear();
        candidate.Bytes.CopyTo(mappingKeyInput.Slice(12, Address.Size));
        mappingKeyInput[63] = ValidatorsStateSlot;
        ValueHash256 slotHash = ValueKeccak.Compute(mappingKeyInput);
        UInt256 slot = new(slotHash.Bytes, isBigEndian: true);

        StorageCell cell = new(ContractAddress!, slot);
        ReadOnlySpan<byte> storageValue = worldState.Get(cell);

        // Right-align into a 32-byte buffer and take the last 20 bytes to get the owner address,
        // mirroring Go's GetOwner: common.HexToAddress(GetState(...).Hex()).
        // Unknown candidates return all-zero bytes → Address.Zero.
        Span<byte> raw = stackalloc byte[32];
        storageValue.CopyTo(raw.Slice(32 - storageValue.Length));
        return new Address(raw.Slice(32 - Address.Size));
    }

    public Address[] GetVoters(IWorldState worldState, Address candidate)
    {
        // mapping(address => address[]) voters: the length sits at the mapping slot, the entries at keccak of it.
        UInt256 arraySlot = MappingSlot(candidate, (UInt256)(byte)CandidateContractSlots.Voters);
        UInt256 length = ReadSlot(worldState, arraySlot);
        if (length.IsZero)
        {
            return [];
        }

        Span<byte> arraySlotBytes = stackalloc byte[32];
        arraySlot.ToBigEndian(arraySlotBytes);
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
        ReadOnlySpan<byte> value = worldState.Get(new StorageCell(ContractAddress!, slot));
        return value.IsEmpty ? UInt256.Zero : new UInt256(value, isBigEndian: true);
    }

    private Address ReadAddress(IWorldState worldState, in UInt256 slot)
    {
        ReadOnlySpan<byte> value = worldState.Get(new StorageCell(ContractAddress!, slot));

        // Storage values are stored trimmed, so right-align before taking the low 20 bytes.
        Span<byte> raw = stackalloc byte[32];
        raw.Clear();
        value.CopyTo(raw.Slice(32 - value.Length));
        return new Address(raw.Slice(32 - Address.Size));
    }

    public Address[] GetCandidates(BlockHeader blockHeader)
    {
        CallInfo callInfo = new(blockHeader, "getCandidates", Address.SystemUser);
        using IReadOnlyTxProcessorSource source = readOnlyTxProcessingEnvFactory.Create();
        IConstantContract constant = GetConstant(source);
        object[] result = constant.Call(callInfo);
        return (Address[])result[0]!;
    }

    public Address[] GetCandidates(ITransactionProcessor transactionProcessor, BlockHeader blockHeader)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, "getCandidates", GenerateTransaction<Transaction>(ContractAddress, "getCandidates", Address.SystemUser), true);
        object[] decoded = DecodeReturnData("getCandidates", result);
        return (Address[])decoded[0]!;
    }

    /// <summary>
    /// Optimization to get candidates directly from storage without going through EVM call
    /// </summary>
    /// <param name="header"></param>
    /// <returns></returns>
    public Address[] GetCandidatesFromState(BlockHeader header)
    {
        CandidateContractSlots variableSlot = CandidateContractSlots.Candidates;
        Span<byte> input = [(byte)variableSlot];
        UInt256 slot = new(Keccak.Compute(input).Bytes);
        using IReadOnlyTxProcessorSource txProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope source = txProcessorSource.Build(header);
        IWorldState worldState = source.WorldState;
        ReadOnlySpan<byte> storageCell = worldState.Get(new StorageCell(ContractAddress, slot));
        UInt256 length = new(storageCell);
        Address[] candidates = new Address[(ulong)length];
        for (int i = 0; i < length; i++)
        {
            UInt256 key = CalculateArrayKey(slot, (ulong)i, 1);
            candidates[i] = new Address(worldState.Get(new StorageCell(ContractAddress, key)));
        }
        return candidates;
    }

    private UInt256 CalculateArrayKey(UInt256 slot, ulong index, ulong size) => slot + new UInt256(index * size);

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
