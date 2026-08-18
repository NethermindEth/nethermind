// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct StatelessValidationResult
{
    public Hash256 NewPayloadRequestRoot { get; set; }

    public bool IsSuccess { get; set; }

    public ulong ChainId { get; set; }

    /// <summary>The schema id of the input this result answers, echoed back to identify the fork and revision.</summary>
    public ushort SchemaId { get; set; }
}
