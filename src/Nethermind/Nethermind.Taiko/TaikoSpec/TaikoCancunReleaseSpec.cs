// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoOntakeReleaseSpec : Cancun, ITaikoReleaseSpec
{

    public TaikoOntakeReleaseSpec()
    {
        IsOntakeEnabled = true;
        IsPacayaEnabled = false;
    }

    public bool IsOntakeEnabled { get; set; }
    public bool IsPacayaEnabled { get; set; }
}

public class TaikoPacayaReleaseSpec : TaikoOntakeReleaseSpec, ITaikoReleaseSpec
{

    public TaikoPacayaReleaseSpec()
    {
        IsOntakeEnabled = true;
        IsPacayaEnabled = true;
    }

}
