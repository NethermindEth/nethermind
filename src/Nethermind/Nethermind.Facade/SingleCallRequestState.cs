// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Facade;

public class SingleCallRequestState : IBlobBaseFeeOverrideProvider
{
    public UInt256? BlobBaseFeeOverride { get; set; }
}
