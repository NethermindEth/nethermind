// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.Trace;

internal static class ParityTraceActionExtensions
{
    /// <summary>The recipient side a trace record reports, used to match <c>toAddress</c> in <c>trace_filter</c>.</summary>
    /// <remarks>
    /// A reward reports its author and a creation reports the address from its result. A failed creation has no
    /// recipient, even though its action still carries the address it would have created.
    /// </remarks>
    public static Address? GetRecipient(this ParityTraceAction action) => action.Type switch
    {
        "reward" => action.Author,
        "create" => action.Result?.Address,
        _ => action.To,
    };
}
