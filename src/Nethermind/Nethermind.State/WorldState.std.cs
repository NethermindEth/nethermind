// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

public sealed partial class WorldState
{
    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
    {
        DebugGuardInScope();
        return _stateProvider.GetCode(in codeHash);
    }
}
