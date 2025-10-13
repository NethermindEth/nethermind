// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Int256;

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
}
