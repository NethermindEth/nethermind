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

    public OptimismEthereumEcdsa(IEthereumEcdsa ethereumEcdsa)
    {
        _ethereumEcdsa = ethereumEcdsa;
    }

    public void Sign(PrivateKey privateKey, Transaction tx, bool isEip155Enabled = true) => _ethereumEcdsa.Sign(privateKey, tx, isEip155Enabled);

    public Address? RecoverAddress(Transaction tx, bool useSignatureChainId = false)
    {
        if (tx.Signature is null && tx.IsOPSystemTransaction)
        {
            return Address.Zero;
        }
        return _ethereumEcdsa.RecoverAddress(tx, useSignatureChainId);
    }

    public Address? RecoverAddress(Signature signature, Hash256 message) => _ethereumEcdsa.RecoverAddress(signature, message);

    public Address? RecoverAddress(Span<byte> signatureBytes, Hash256 message) => _ethereumEcdsa.RecoverAddress(signatureBytes, message);

    public bool Verify(Address sender, Transaction tx) => _ethereumEcdsa.Verify(sender, tx);
}
