// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Network.Discovery.Messages;

public class NodeIdResolver : INodeIdResolver
{
    private readonly IEcdsa _ecdsa;

    public NodeIdResolver(IEcdsa ecdsa)
    {
        _ecdsa = ecdsa;
    }

    public PublicKey GetNodeId(ReadOnlySpan<byte> signature, int recoveryId, Span<byte> typeAndData)
    {
        return _ecdsa.RecoverPublicKey(new Signature(signature, recoveryId), Keccak.Compute(typeAndData));
    }
}
