// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Blockchain.ValidatorExit;

// https://eips.ethereum.org/EIPS/eip-7002#block-processing
public class ValidatorExitEipHandler : IValidatorExitEipHandler
{
    // private static readonly UInt256 ExcessExitsStorageSlot = 0;
    // private static readonly UInt256 ExitCountStorageSlot = 1;
    private static readonly UInt256 ExitMessageQueueHeadStorageSlot = 2;
    private static readonly UInt256 ExitMessageQueueTailStorageSlot = 3;
    private static readonly UInt256 ExitMessageQueueStorageOffset = 4;
    private static readonly UInt256 MaxExitsPerBlock = 16;

    // Reads validator exit information from the precompile
    public ValidatorExit[] CalculateValidatorExits(IReleaseSpec spec, IWorldState state)
    {
        StorageCell queueHeadIndexCell = new(spec.Eip7002ContractAddress, ExitMessageQueueHeadStorageSlot);
        StorageCell queueTailIndexCell = new(spec.Eip7002ContractAddress, ExitMessageQueueTailStorageSlot);

        UInt256 queueHeadIndex = new(state.Get(queueHeadIndexCell));
        UInt256 queueTailIndex = new(state.Get(queueTailIndexCell));

        UInt256 numExitsInQueue = queueTailIndex - queueHeadIndex;
        UInt256 numExitsToDeque = UInt256.Min(numExitsInQueue, MaxExitsPerBlock);

        var validatorExits = new ValidatorExit[(int)numExitsToDeque];
        for (UInt256 i = 0; i < numExitsToDeque; ++i)
        {
            UInt256 queueStorageSlot = ExitMessageQueueStorageOffset + (queueHeadIndex + i) * 3;
            StorageCell sourceAddressCell = new(spec.Eip7002ContractAddress, queueStorageSlot);
            StorageCell validatorAddressFirstCell = new(spec.Eip7002ContractAddress, queueStorageSlot + 1);
            StorageCell validatorAddressSecondCell = new(spec.Eip7002ContractAddress, queueStorageSlot + 2);
            Address sourceAddress = new(state.Get(sourceAddressCell)[..20].ToArray());
            byte[] validatorPubkey =
                state.Get(validatorAddressFirstCell)[..32].ToArray()
                    .Concat(state.Get(validatorAddressSecondCell)[..16].ToArray())
                    .ToArray();
            validatorExits[(int)i] = new ValidatorExit { SourceAddress = sourceAddress, ValidatorPubkey = validatorPubkey };
        }

        return validatorExits;
    }
}
