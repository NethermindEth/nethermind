// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.AuRa
{
    public interface IAuRaStepCalculator
    {
        long CurrentStep { get; }
        TimeSpan TimeToNextStep { get; }
        TimeSpan TimeToStep(long step);

        long CurrentStepDuration { get; }
    }
}
