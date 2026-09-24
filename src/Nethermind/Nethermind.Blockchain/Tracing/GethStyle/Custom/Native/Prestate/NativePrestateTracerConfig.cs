// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;

public class NativePrestateTracerConfig
{
    public bool DiffMode { get; init; }

    /// <summary>Gets whether to omit contract code bytes. Defaults to false; code hashes remain included.</summary>
    public bool DisableCode { get; init; }

    /// <summary>Gets whether to omit storage from prestate and state diffs. Defaults to false.</summary>
    public bool DisableStorage { get; init; }
}
