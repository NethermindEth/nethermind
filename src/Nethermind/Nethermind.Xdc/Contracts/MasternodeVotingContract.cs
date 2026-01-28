// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using System;
using System.Linq;

namespace Nethermind.Xdc.Contracts;

internal class MasternodeVotingContract : Contract, IMasternodeVotingContract
{
    private readonly IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory;
    private readonly ITransactionProcessor transactionProcessor;

    public MasternodeVotingContract(
        IAbiEncoder abiEncoder,
        Address contractAddress,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        ITransactionProcessor transactionProcessor)
        : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), CreateAbiDefinition())
    {
        this.readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
        this.transactionProcessor = transactionProcessor;
    }

    private static AbiDefinition CreateAbiDefinition()
    {
        AbiDefinitionParser abiDefinitionParser = new AbiDefinitionParser();
        return abiDefinitionParser.Parse(typeof(MasternodeVotingContract));
    }

    public UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate)
    {
        CallInfo callInfo = new CallInfo(blockHeader, "getCandidateCap", Address.SystemUser, candidate);
        IConstantContract constant = GetConstant(readOnlyTxProcessingEnvFactory.Create());
        object[] result = constant.Call(callInfo);
        if (result.Length != 1)
            throw new InvalidOperationException("Expected 'getCandidateCap' to return exactly one result.");

        return (UInt256)result[0]!;
    }

    public Address GetCandidateOwner(BlockHeader blockHeader, Address candidate)
    {
        CallInfo callInfo = new CallInfo(blockHeader, "getCandidateOwner", Address.SystemUser, candidate);
        IConstantContract constant = GetConstant(readOnlyTxProcessingEnvFactory.Create());
        object[] result = constant.Call(callInfo);
        if (result.Length != 1)
            throw new InvalidOperationException("Expected 'getCandidateOwner' to return exactly one result.");

        return (Address)result[0]!;
    }

    public Address GetCandidateOwnerDuringProcessing(BlockHeader blockHeader, Address candidate)
    {
        byte[] result = base.CallCore(transactionProcessor, blockHeader, "getCandidateOwner", GenerateTransaction<Transaction>(ContractAddress, "getCandidateOwner", Address.SystemUser, candidate), true);
        if (result.Length != 20)
            throw new InvalidOperationException("Expected 'getCandidateOwner' to return exactly one result.");
        return new Address(result);
    }

    public Address[] GetCandidates(BlockHeader blockHeader)
    {
        CallInfo callInfo = new CallInfo(blockHeader, "getCandidates", Address.SystemUser);
        IConstantContract constant = GetConstant(readOnlyTxProcessingEnvFactory.Create());
        object[] result = constant.Call(callInfo);
        return (Address[])result[0]!;
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
        UInt256 slot = new UInt256(Keccak.Compute(input).Bytes);
        IReadOnlyTxProcessorSource txProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope source = txProcessorSource.Build(header);
        IWorldState worldState = source.WorldState;
        ReadOnlySpan<byte> storageCell = worldState.Get(new StorageCell(ContractAddress, slot));
        var length = new UInt256(storageCell);
        Address[] candidates = new Address[(ulong)length];
        for (int i = 0; i < length; i++)
        {
            UInt256 key = CalculateArrayKey(slot, (ulong)i, 1);
            candidates[i] = new Address(worldState.Get(new StorageCell(ContractAddress, key)));
        }
        return candidates;
    }

    private UInt256 CalculateArrayKey(UInt256 slot, ulong index, ulong size)
    {
        return slot + new UInt256(index * size);
    }

    /// <summary>
    /// Returns an array of masternode candidates sorted by stake
    /// </summary>
    /// <param name="blockHeader"></param>
    /// <returns></returns>
    public Address[] GetCandidatesByStake(BlockHeader blockHeader)
    {
        Address[] candidates = GetCandidates(blockHeader);

        using var candidatesAndStake = new ArrayPoolList<CandidateStake>(candidates.Length);
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
