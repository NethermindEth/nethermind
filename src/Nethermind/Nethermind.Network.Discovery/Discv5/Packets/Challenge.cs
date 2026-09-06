// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Nethermind.Network.Discovery.Discv5.Packets;

internal sealed class Challenge : IDisposable
{
    private readonly ReadOnlyMemory<byte> _challengeData;
    private readonly byte[]? _ownedChallengeData;

    public Challenge(ulong enrSequence, ReadOnlyMemory<byte> challengeData)
    {
        EnrSequence = enrSequence;
        _challengeData = challengeData;
    }

    public Challenge(ulong enrSequence, byte[] ownedChallengeData)
    {
        EnrSequence = enrSequence;
        _ownedChallengeData = ownedChallengeData;
        _challengeData = ownedChallengeData;
    }

    public ulong EnrSequence { get; }

    public ReadOnlySpan<byte> ChallengeData => _challengeData.Span;

    public void Dispose()
    {
        if (_ownedChallengeData is not null)
        {
            CryptographicOperations.ZeroMemory(_ownedChallengeData);
        }
    }
}
