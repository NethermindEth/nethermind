// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.Qbft.Config;

[ConfigCategory(Description = "QBFT consensus node options. Chain-wide QBFT parameters live in the chainspec engine section.")]
public interface IQbftConfig : IConfig
{
    [ConfigItem(Description = "Whether to change round as soon as f+1 round-change messages for a higher round arrive, without waiting for the local round timer. Besu's --Xqbft-enable-early-round-change.", DefaultValue = "false")]
    bool EarlyRoundChange { get; set; }

    [ConfigItem(Description = "Whether to emit the pre-Besu-26.1.0 message encoding without the block access list slot in Proposal and RoundChange messages. Both encodings are always accepted.", DefaultValue = "false")]
    bool LegacyMessageEncoding { get; set; }
}

public class QbftConfig : IQbftConfig
{
    public bool EarlyRoundChange { get; set; }
    public bool LegacyMessageEncoding { get; set; }
}
