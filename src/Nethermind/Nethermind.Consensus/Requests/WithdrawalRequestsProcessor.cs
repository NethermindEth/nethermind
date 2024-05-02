// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

// https://eips.ethereum.org/EIPS/eip-7002#block-processing
public class WithdrawalRequestsProcessor : IWithdrawalRequestsProcessor
{
    private static readonly UInt256 ExcessWithdrawalRequestsStorageSlot = 0;
    private static readonly UInt256 WithdrawalRequestCountStorageSlot = 1;
    private static readonly UInt256 WithdrawalRequestQueueHeadStorageSlot = 2;
    private static readonly UInt256 WithdrawalRequestQueueTailStorageSlot = 3;
    private static readonly UInt256 WithdrawalRequestQueueStorageOffset = 4;
    private static readonly UInt256 MaxWithdrawalRequestsPerBlock = 16;
    private static readonly UInt256 TargetWithdrawalRequestsPerBlock = 2;

    // Will be moved to system transaction
    public IEnumerable<WithdrawalRequest> ReadWithdrawalRequests(IReleaseSpec spec, IWorldState state, Block block)
    {
        if (!spec.IsEip7002Enabled)
            yield break;

        Address eip7002Account = spec.Eip7002ContractAddress;
        if (!state.AccountExists(eip7002Account))
            yield break;

        // Reads validator exit information from the precompile
        StorageCell queueHeadIndexCell = new(spec.Eip7002ContractAddress, WithdrawalRequestQueueHeadStorageSlot);
        StorageCell queueTailIndexCell = new(spec.Eip7002ContractAddress, WithdrawalRequestQueueTailStorageSlot);

        UInt256 queueHeadIndex = new(state.Get(queueHeadIndexCell));
        UInt256 queueTailIndex = new(state.Get(queueTailIndexCell));

        UInt256 numInQueue = queueTailIndex - queueHeadIndex;
        UInt256 numDequeued = UInt256.Min(numInQueue, MaxWithdrawalRequestsPerBlock);

        for (UInt256 i = 0; i < numDequeued; ++i)
        {
            UInt256 queueStorageSlot = WithdrawalRequestQueueStorageOffset + (queueHeadIndex + i) * 3;
            StorageCell sourceAddressCell = new(spec.Eip7002ContractAddress, queueStorageSlot);
            StorageCell validatorAddressFirstCell = new(spec.Eip7002ContractAddress, queueStorageSlot + 1);
            StorageCell validatorAddressSecondCell = new(spec.Eip7002ContractAddress, queueStorageSlot + 2);
            Address sourceAddress = new(state.Get(sourceAddressCell)[..20].ToArray());
            byte[] validatorPubKey = new byte[48];
            state.Get(validatorAddressFirstCell)[..32].CopyTo(validatorPubKey[..32]);
            state.Get(validatorAddressSecondCell)[..16].CopyTo(validatorPubKey[32..]);
            ulong amount = state.Get(validatorAddressSecondCell)[16..24].ToULongFromBigEndianByteArrayWithoutLeadingZeros(); // ToDo write tests to extension method
            yield return new WithdrawalRequest { SourceAddress = sourceAddress, ValidatorPubkey = validatorPubKey, Amount = amount };
        }

        UInt256 newQueueHeadIndex = queueHeadIndex + numDequeued;
        if (newQueueHeadIndex == queueTailIndex)
        {
            state.Set(queueHeadIndexCell, UInt256.Zero.ToBigEndian());
            state.Set(queueTailIndexCell, UInt256.Zero.ToBigEndian());
        }
        else
        {
            state.Set(queueHeadIndexCell, newQueueHeadIndex.ToBigEndian());
        }

        UpdateExcessExits(spec, state);
        ResetExitCount(spec, state);
    }

    private void UpdateExcessExits(IReleaseSpec spec, IWorldState state)
    {
        StorageCell previousExcessCell = new(spec.Eip7002ContractAddress, ExcessWithdrawalRequestsStorageSlot);
        StorageCell countCell = new(spec.Eip7002ContractAddress, WithdrawalRequestCountStorageSlot);

        UInt256 previousExcess = new(state.Get(previousExcessCell));
        UInt256 count = new(state.Get(countCell));

        UInt256 newExcess = 0;
        if (previousExcess + count > TargetWithdrawalRequestsPerBlock)
        {
            newExcess = previousExcess + count - TargetWithdrawalRequestsPerBlock;
        }

        state.Set(previousExcessCell, newExcess.ToBigEndian());
    }

    private void ResetExitCount(IReleaseSpec spec, IWorldState state)
    {
        StorageCell countCell = new(spec.Eip7002ContractAddress, WithdrawalRequestCountStorageSlot);
        state.Set(countCell, UInt256.Zero.ToBigEndian());
    }
}
