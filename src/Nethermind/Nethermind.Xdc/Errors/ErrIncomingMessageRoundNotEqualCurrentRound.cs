// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;
public class ErrIncomingMessageRoundNotEqualCurrentRound : Exception
{
    public string Type { get; }
    public ulong IncomingRound { get; }
    public ulong CurrentRound { get; }

    public ErrIncomingMessageRoundNotEqualCurrentRound(string type, ulong incomingRound, ulong currentRound)
    {
        Type = type;
        IncomingRound = incomingRound;
        CurrentRound = currentRound;
    }

    public override string Message =>
        $"{Type} message round number: {IncomingRound} does not match currentRound: {CurrentRound}";
}
