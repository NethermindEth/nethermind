// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Core.Extensions;

public static class SpecProviderExtensions
{
    /// <summary>
    /// this method is here only for getting spec for 1559.
    /// Reason of adding is that at sometime we dont know the Timestamp.
    /// </summary>
    /// <param name="specProvider"></param>
    /// <param name="blockNumber"></param>
    /// <returns>ReleaseSpec that has the values for EIP1559 correct but not the rest.</returns>
    public static IReleaseSpec GetSpecFor1559(this ISpecProvider specProvider, long blockNumber)
    {
        return specProvider.GetSpec(blockNumber, null);
    }
}
