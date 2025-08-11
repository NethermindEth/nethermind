// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Taiko.TaikoSpec;

public interface ITaikoReleaseSpec : IReleaseSpec
{
    public bool IsOntakeEnabled { get; }
    public bool IsPacayaEnabled { get; }
    public bool UseSurgeGasPriceOracle { get; }
    public Address TaikoL2Address { get; }
}
