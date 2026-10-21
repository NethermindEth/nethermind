// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class OnUpdateMainChainArgs(IReadOnlyList<BlockHeader> headers, bool wereProcessed) : EventArgs
{
    public IReadOnlyList<BlockHeader> Headers { get; } = headers;

    public bool WereProcessed { get; } = wereProcessed;
}
