// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.GnosisForks;

public class PragueGnosis : Forks.Prague
{
    protected PragueGnosis() : base()
    {
        IsEip4844FeeCollectorEnabled = true;
    }
}
