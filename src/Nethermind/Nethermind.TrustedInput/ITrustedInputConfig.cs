// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.TrustedInput;

/// <summary>Controls trusted zkVM input capture, retention, and optional file output.</summary>
public interface ITrustedInputConfig : IConfig
{
    /// <summary>Whether to capture inputs and register the RPC module. Defaults to false.</summary>
    [ConfigItem(Description = "Whether to capture trusted zkVM guest inputs while processing blocks, and serve them over `trustedinput_getInput`. A capture runs in the background after the import: it recovers each sender's and ECRECOVER key once more for the hints and encodes the input, and the last `CacheSize` inputs stay in memory, each the size of the block's witness, transactions and codes. Capture skips blocks while busy; RPC regenerates missing inputs.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>The maximum number of completed inputs retained in memory. Defaults to 64.</summary>
    [ConfigItem(Description = "The number of most recent captured inputs kept in memory.", DefaultValue = "64")]
    int CacheSize { get; set; }

    /// <summary>The output directory for captured .nti files, or null to disable file output. Defaults to null.</summary>
    [ConfigItem(Description = "The directory each captured input is also written to, as `<number>-<hash>.nti`. No files are written when unset.", DefaultValue = "null")]
    string? OutputDirectory { get; set; }
}
