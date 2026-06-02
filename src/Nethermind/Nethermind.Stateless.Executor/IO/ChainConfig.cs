// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ChainConfig
{
    public ulong ChainId { get; set; }

    public ForkConfig ActiveFork { get; set; }

    /// <summary>
    /// Optional UTF-8 JSON `{"config": ...}` envelope describing the chain spec
    /// directly. When non-empty, the stateless executor builds a custom
    /// <see cref="Nethermind.Core.Specs.ISpecProvider"/> from it (timestamp-based
    /// forks only — Shanghai/Cancun/Prague/Osaka) and uses that instead of the
    /// hardcoded provider keyed off <see cref="ChainId"/>. Lets EEST-style
    /// fixtures with synthetic fork activations validate without needing a
    /// matching real chain.
    /// </summary>
    [SszList(0x10_0000)]
    public byte[] ChainConfigJson { get; set; }
}
