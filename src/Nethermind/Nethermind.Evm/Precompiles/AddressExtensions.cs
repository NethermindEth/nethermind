// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public static class AddressExtensions
{
    /// <summary>
    /// Determines if the specified address is a precompiled contract according to the release specification.
    /// This method delegates to IReleaseSpec.IsPrecompile to allow chain-specific implementations
    /// to define their own precompile logic
    /// </summary>
    /// <param name="address">The address to check for precompile status.</param>
    /// <param name="releaseSpec">The release specification that defines which precompiles are available.</param>
    /// <returns>True if the address is a precompiled contract; otherwise, false.</returns>
    public static bool IsPrecompile(this Address address, IReleaseSpec releaseSpec)
    {
        return releaseSpec.IsPrecompile(address);
    }
}
