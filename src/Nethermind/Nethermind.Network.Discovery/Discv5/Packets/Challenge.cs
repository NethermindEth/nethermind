// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Nethermind.Network.Discovery.Discv5.Packets;

internal sealed record Challenge(byte[] RequestNonce, byte[] IdNonce, ulong EnrSequence, byte[] ChallengeData) : IDisposable
{
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(RequestNonce);
        CryptographicOperations.ZeroMemory(IdNonce);
        CryptographicOperations.ZeroMemory(ChallengeData);
    }
}
