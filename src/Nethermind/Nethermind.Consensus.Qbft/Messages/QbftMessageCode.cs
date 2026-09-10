// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Qbft.Messages;

/// <summary>Message codes of the <c>istanbul/100</c> sub-protocol (Besu's <c>QbftV1</c>).</summary>
/// <remarks>
/// Codes below <see cref="Proposal"/> are unused; the space is still <see cref="MessageSpace"/> wide so
/// the adaptive message ids negotiated with Besu line up.
/// </remarks>
public static class QbftMessageCode
{
    public const int Proposal = 0x12;
    public const int Prepare = 0x13;
    public const int Commit = 0x14;
    public const int RoundChange = 0x15;
    public const int MessageSpace = 0x16;

    public static bool IsValid(int code) => code is Proposal or Prepare or Commit or RoundChange;

    public static string Name(int code) => code switch
    {
        Proposal => nameof(Proposal),
        Prepare => nameof(Prepare),
        Commit => nameof(Commit),
        RoundChange => nameof(RoundChange),
        _ => "Invalid",
    };
}
