// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public interface IOverridableCodeInfoRepository : ICodeInfoRepository
{
    /// <summary>Serves <paramref name="value"/> as the code of <paramref name="key"/> until <see cref="ResetOverrides"/>.</summary>
    /// <remarks>
    /// A non-default <see cref="CodeInfo.CodeHash"/> on <paramref name="value"/> is trusted as the keccak of its code
    /// and may become the key under which the code is shared with other overrides.
    /// </remarks>
    void SetCodeOverride(IReleaseSpec vmSpec, Address key, CodeInfo value);
    void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr);
    void ResetOverrides();
    void ResetPrecompileOverrides();
}
