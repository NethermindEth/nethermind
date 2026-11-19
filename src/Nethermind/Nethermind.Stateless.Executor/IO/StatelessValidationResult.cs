// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct StatelessValidationResult
{
    [SszField(0)]
    public Hash256 NewPayloadRequestRoot { get; set; }

    [SszField(1)]
    public bool IsSuccess { get; set; }

    [SszField(2)]
    public ulong ChainId { get; set; }

    /// <summary>Gets or sets the schema of the input that was decoded and executed.</summary>
    /// <remarks>Zero is the sentinel reported when the input bytes cannot be decoded.</remarks>
    [SszField(3)]
    public ushort SchemaId { get; set; }
}
