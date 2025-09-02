// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;
public class IncomingMessageRoundNotEqualCurrentRoundException : Exception
{
    public string Type { get; }
    public ulong IncomingRound { get; }
    public ulong CurrentRound { get; }

    public IncomingMessageRoundNotEqualCurrentRoundException(string type, ulong incomingRound, ulong currentRound)
        : base($"{type} message round number: {incomingRound} does not match currentRound: {currentRound}")
    {
        Type = type;
        IncomingRound = incomingRound;
        CurrentRound = currentRound;
    }

}
