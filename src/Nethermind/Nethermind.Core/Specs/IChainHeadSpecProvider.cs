// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// Provides the spec (list of enabled EIPs) at the current chain head.
    /// </summary>
    public interface IChainHeadSpecProvider : ISpecProvider
    {
        IReleaseSpec GetCurrentHeadSpec();
    }
}
