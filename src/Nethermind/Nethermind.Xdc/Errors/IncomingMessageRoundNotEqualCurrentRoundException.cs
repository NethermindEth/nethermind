// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;
public class IncomingMessageRoundNotEqualCurrentRoundException(ulong incomingRound, ulong currentRound, Exception? innerException = null)
    : Exception($"message round number: {incomingRound} does not match currentRound: {currentRound}", innerException)
{
    public ulong IncomingRound { get; } = incomingRound;
    public ulong CurrentRound { get; } = currentRound;
}
