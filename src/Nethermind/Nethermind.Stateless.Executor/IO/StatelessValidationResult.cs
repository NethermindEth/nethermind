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

    public ChainConfig ChainConfig { get; set; }
}
