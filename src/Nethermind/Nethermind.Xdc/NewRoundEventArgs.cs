// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc;

public class NewRoundEventArgs(ulong round, int previousRoundTimeouts) : EventArgs
{
    public ulong NewRound { get; } = round;
    public int PreviousRoundTimeouts { get; } = previousRoundTimeouts;
}
