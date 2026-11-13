// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>The creation transaction and immediate creator of a deployed contract.</summary>
public sealed class OtsContractCreator
{
    public required Hash256 Hash { get; init; }
    public required Address Creator { get; init; }
}
