// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoReleaseSpec : ReleaseSpec, ITaikoReleaseSpec
{
    public bool IsOntakeEnabled { get; set; }
    public bool IsPacayaEnabled { get; set; }
}
