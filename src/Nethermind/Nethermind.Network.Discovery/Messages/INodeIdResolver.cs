// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Messages;

public interface INodeIdResolver
{
    PublicKey GetNodeId(ReadOnlySpan<byte> signature, int recoveryId, Span<byte> typeAndData);
}
