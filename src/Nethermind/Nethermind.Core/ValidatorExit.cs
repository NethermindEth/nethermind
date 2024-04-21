// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.ValidatorExit;

public struct ValidatorExit
{
    public ValidatorExit(Address sourceAddress, byte[] validatorPubkey, ulong amount)
    {
        SourceAddress = sourceAddress;
        ValidatorPubkey = validatorPubkey;
        Amount = amount;
    }

    public Address SourceAddress;
    public byte[] ValidatorPubkey;
    public ulong Amount;
}
