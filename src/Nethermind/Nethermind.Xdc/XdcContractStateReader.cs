// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class XdcContractStateReader(IWorldState worldState, ISpecProvider specProvider)
{

    public Address[] GetCandidates(XdcBlockHeader header)
    {
        var spec = specProvider.GetXdcSpec(header, header.ExtraConsensusData.BlockRound);
        var variableSlot = CandidateContractSlots.Candidates;
        Span<byte> input = [(byte)variableSlot];
        var slot = new UInt256(Keccak.Compute(input).Bytes);
        using var state = worldState.BeginScope(header);
        ReadOnlySpan<byte> storageCell = worldState.Get(new StorageCell(spec.MasternodeVotingContract, slot));
        var length = new UInt256(storageCell);
        Address[] candidates = new Address[(ulong)length];
        for (int i = 0; i < length; i++)
        {
            var key = CalculateArrayKey(slot, (ulong)i, 1);
            candidates[i] = new Address(worldState.Get(new StorageCell(spec.MasternodeVotingContract, key)));
        }
        return candidates;
    }

    private UInt256 CalculateArrayKey(UInt256 slot, ulong index, ulong size)
    {
        return slot + new UInt256(index * size);
    }

    public UInt256 GetCandidateStake(Address candidate)
    {
        var variableSlot = CandidateContractSlots.ValidatorsState;

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


