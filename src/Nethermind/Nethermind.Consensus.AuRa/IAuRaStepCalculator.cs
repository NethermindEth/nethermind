// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.AuRa
{
    public interface IAuRaStepCalculator
    {
        ulong CurrentStep { get; }
        TimeSpan TimeToNextStep { get; }
        TimeSpan TimeToStep(ulong step);

        ulong CurrentStepDuration { get; }
    }
}
