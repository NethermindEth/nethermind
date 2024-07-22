// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.TxPool;
using System;

namespace Nethermind.Consensus
{
    public interface ISigner : ITxSigner
    {
        // TODO: this breaks the encapsulation of the key inside the signer, would like to see this removed
        PrivateKey? Key { get; }
        Address Address { get; }
        Signature Sign(Hash256 message);
        bool CanSign { get; }
    }
}
