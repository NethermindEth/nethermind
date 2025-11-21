// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Xdc.Contracts;
using System;
using System.Linq;

namespace Nethermind.Xdc.Contracts;
internal class MasternodeVotingContract : Contract, IMasternodeVotingContract
{
    private readonly IWorldState _worldState;
    private IConstantContract _constant;

    public MasternodeVotingContract(
        IWorldState worldState,
        IAbiEncoder abiEncoder,
        Address contractAddress,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)), CreateAbiDefinition())
    {
        _constant = GetConstant(readOnlyTxProcessorSource);
        _worldState = worldState;
    }

    private static AbiDefinition CreateAbiDefinition()
    {
        var abiDefinitionParser = new AbiDefinitionParser();
        return abiDefinitionParser.Parse(typeof(MasternodeVotingContract));
    }

    public UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate)
    {
        var callInfo = new CallInfo(blockHeader, "getCandidateCap", Address.SystemUser, candidate);
        var result = this._constant.Call(callInfo);
        if (result.Length != 1)
            throw new InvalidOperationException("Expected 'getCandidateCap' to return exactly one result.");

        return (UInt256)result[0]!;
    }

    public Address[] GetCandidates(BlockHeader blockHeader)
    {
        var callInfo = new CallInfo(blockHeader, "getCandidates", Address.SystemUser);
        var result = this._constant.Call(callInfo);
        return (Address[])result[0]!;
    }

    /// <summary>
    /// Optimization to get candidates directly from storage without going through EVM call
    /// </summary>
    /// <param name="header"></param>
    /// <returns></returns>
    public Address[] GetCandidatesFromState(BlockHeader header)
    {
        var variableSlot = CandidateContractSlots.Candidates;
        Span<byte> input = [(byte)variableSlot];
        var slot = new UInt256(Keccak.Compute(input).Bytes);
        using var state = _worldState.BeginScope(header);
        ReadOnlySpan<byte> storageCell = _worldState.Get(new StorageCell(ContractAddress, slot));
        var length = new UInt256(storageCell);
        Address[] candidates = new Address[(ulong)length];
        for (int i = 0; i < length; i++)
        {
            var key = CalculateArrayKey(slot, (ulong)i, 1);
            candidates[i] = new Address(_worldState.Get(new StorageCell(ContractAddress, key)));
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
        var candidates = GetCandidates(blockHeader);

        using var candidatesAndStake = new ArrayPoolList<CandidateStake>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (candidate == Address.Zero)
                continue;

            new CandidateStake()
            {
                Address = candidate,
                Stake = GetCandidateStake(blockHeader, candidate)
            };
        }
        candidatesAndStake.Sort((x, y) => y.Stake.CompareTo(x.Stake));

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
