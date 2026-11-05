// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Facade;

public interface IBlobBaseFeeOverrideProvider
{
    UInt256? BlobBaseFeeOverride { get; }
}
