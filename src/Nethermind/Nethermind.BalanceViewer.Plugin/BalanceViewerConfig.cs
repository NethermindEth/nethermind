// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BalanceViewer.Plugin;

public class BalanceViewerConfig : IBalanceViewerConfig
{
    public bool Enabled { get; set; } = true;
    public string SiblingProbePorts { get; set; } = "8545,8546,8547,8548,8549,8550";
}
