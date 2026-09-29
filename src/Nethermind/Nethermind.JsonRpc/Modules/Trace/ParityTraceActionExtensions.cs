// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.Trace;

internal static class ParityTraceActionExtensions
{
    /// <summary>
    /// The recipient side a record reports: a reward's author, and a creation's address from its result, which a
    /// failed creation does not report even though its action carries the address it would have created.
    /// </summary>
    public static Address? GetRecipient(this ParityTraceAction action) => action.Type switch
    {
        "reward" => action.Author,
        "create" => action.Result?.Address,
        _ => action.To,
    };
}
