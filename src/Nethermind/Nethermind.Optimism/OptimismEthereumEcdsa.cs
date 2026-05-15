// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Optimism;

public class OptimismEthereumEcdsa(IEthereumEcdsa ethereumEcdsa) : Ecdsa, IEthereumEcdsa
{
    private readonly IEthereumEcdsa _ethereumEcdsa = ethereumEcdsa;

    public ulong ChainId => _ethereumEcdsa.ChainId;

    public Address? RecoverAddress(Signature signature, in ValueHash256 message) => _ethereumEcdsa.RecoverAddress(signature, in message);
}
