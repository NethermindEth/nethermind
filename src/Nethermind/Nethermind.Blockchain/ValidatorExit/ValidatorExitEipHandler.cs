// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.ValidatorExit;

// https://eips.ethereum.org/EIPS/eip-7002#block-processing
public class ValidatorExitEipHandler : IValidatorExitEipHandler
{
    private static readonly UInt256 ExcessWithdrawalRequestsStorageSlot = 0;
    private static readonly UInt256 WithdrawalRequestCountStorageSlot = 1;
    private static readonly UInt256 WithdrawalRequestQueueHeadStorageSlot = 2;
    private static readonly UInt256 WithdrawalRequestQueueTailStorageSlot = 3;
    private static readonly UInt256 WithdrawalRequestQueueStorageOffset = 4;
    private static readonly UInt256 MaxWithdrawalRequestsPerBlock = 16;
    private static readonly UInt256 TargetWithdrawalRequestsPerBlock = 2;



    // Will be moved to system transaction
    public ValidatorExit[] ReadWithdrawalRequests(IReleaseSpec spec, IWorldState state)
    {
        ValidatorExit[] exits = DequeueWithdrawalRequests(spec, state);
        UpdateExcessExits(spec, state);
        ResetExitCount(spec, state);
        return exits;
    }

    // Reads validator exit information from the precompile
    public ValidatorExit[] DequeueWithdrawalRequests(IReleaseSpec spec, IWorldState state)
    {
        StorageCell queueHeadIndexCell = new(spec.Eip7002ContractAddress, WithdrawalRequestQueueHeadStorageSlot);
        StorageCell queueTailIndexCell = new(spec.Eip7002ContractAddress, WithdrawalRequestQueueTailStorageSlot);

        UInt256 queueHeadIndex = new(state.Get(queueHeadIndexCell));
        UInt256 queueTailIndex = new(state.Get(queueTailIndexCell));

        UInt256 numInQueue = queueTailIndex - queueHeadIndex;
        UInt256 numDequeued = UInt256.Min(numInQueue, MaxWithdrawalRequestsPerBlock);

        var validatorExits = new ValidatorExit[(int)numDequeued];
        for (UInt256 i = 0; i < numDequeued; ++i)
        {
            UInt256 queueStorageSlot = WithdrawalRequestQueueStorageOffset + (queueHeadIndex + i) * 3;
            StorageCell sourceAddressCell = new(spec.Eip7002ContractAddress, queueStorageSlot);
            StorageCell validatorAddressFirstCell = new(spec.Eip7002ContractAddress, queueStorageSlot + 1);
            StorageCell validatorAddressSecondCell = new(spec.Eip7002ContractAddress, queueStorageSlot + 2);
            Address sourceAddress = new(state.Get(sourceAddressCell)[..20].ToArray());
            byte[] validatorPubkey =
                state.Get(validatorAddressFirstCell)[..32].ToArray()
                    .Concat(state.Get(validatorAddressSecondCell)[..16].ToArray())
                    .ToArray();
            ulong amount =  state.Get(validatorAddressSecondCell)[16..24].ToArray().ToULongFromBigEndianByteArrayWithoutLeadingZeros(); // ToDo write tests to extension method
            validatorExits[(int)i] = new ValidatorExit { SourceAddress = sourceAddress, ValidatorPubkey = validatorPubkey, Amount = amount };
        }

        UInt256 newQueueHeadIndex = queueHeadIndex + numDequeued;
        if (newQueueHeadIndex == queueTailIndex)
        {
            state.Set(queueHeadIndexCell, UInt256.Zero.ToLittleEndian());
            state.Set(queueTailIndexCell, UInt256.Zero.ToLittleEndian()); // ToDo ToLittleEndian??
        }
        else
        {
            state.Set(queueHeadIndexCell, newQueueHeadIndex.ToLittleEndian());
        }

        return validatorExits;
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

        state.Set(previousExcessCell, newExcess.ToLittleEndian());
    }

    private void ResetExitCount(IReleaseSpec spec, IWorldState state)
    {
        StorageCell countCell = new(spec.Eip7002ContractAddress, WithdrawalRequestCountStorageSlot);
        state.Set(countCell, UInt256.Zero.ToLittleEndian());
    }
}
