// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateRequestState
{
    public bool Validate { get; set; }
    public UInt256? BlobBaseFeeOverride { get; set; }
}
