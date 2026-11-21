// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Eez.Execution.Stateless;

public enum EezStatelessFailure
{
    /// <summary>The input is not a valid window: a block, its witness or their chaining is wrong.</summary>
    Rejected,

    /// <summary>The caller asked for something impossible, e.g. checkpoints that are unordered or out of range.</summary>
    InternalInvariant,
}
