// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Processing;

namespace Nethermind.Consensus;

public interface IMiningConfig : IConfig
{
    [ConfigItem(Description = "Whether to produce blocks.", DefaultValue = "false")]
    bool Enabled { get; set; }
    [ConfigItem(
    Description = "The URL of an external signer like [Clef](https://github.com/ethereum/go-ethereum/blob/master/cmd/clef/tutorial.md).",
    HiddenFromDocs = false,
    DefaultValue = "null")]
    string? Signer { get; set; }

    [ConfigItem(
    Description = "Dumps diagnostic traces of every block this node builds, including each payload rebuild, to the `nethermind-produced-blocks` subdirectory of the system temp directory, keeping the newest 256 files. Accepts the same values as `Init.AutoDump`. For debugging only: Geth-style traces include EVM memory and can take gigabytes per block.",
    HiddenFromDocs = true,
    DefaultValue = "None")]
    DumpOptions DumpProducedBlocks { get; set; }
}
