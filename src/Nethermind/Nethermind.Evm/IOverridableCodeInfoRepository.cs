// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;

namespace Nethermind.Evm;

public interface IOverridableCodeInfoRepository : ICodeInfoRepository
{
    void SetCodeOverwrite(IWorldState worldState, IReleaseSpec vmSpec, Address key, ICodeInfo value, Address? redirectAddress = null);
    public void ResetOverrides();
}
