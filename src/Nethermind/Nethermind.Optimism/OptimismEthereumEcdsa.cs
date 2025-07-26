// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Optimism;

public class OptimismEthereumEcdsa : Ecdsa, IEthereumEcdsa
{
    private readonly IEthereumEcdsa _ethereumEcdsa;

    public ulong ChainId => _ethereumEcdsa.ChainId;

    public OptimismEthereumEcdsa(IEthereumEcdsa ethereumEcdsa)
    {
        _ethereumEcdsa = ethereumEcdsa;
    }
    public Address? RecoverAddress(Signature signature, in ValueHash256 message) => _ethereumEcdsa.RecoverAddress(signature, in message);
}
