// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.ValidatorExit;

public struct ValidatorExit
{
    public ValidatorExit(Address sourceAddress, byte[] validatorPubkey)
    {
        SourceAddress = sourceAddress;
        ValidatorPubkey = validatorPubkey;
    }

    public Address SourceAddress;
    public byte[] ValidatorPubkey;
}
