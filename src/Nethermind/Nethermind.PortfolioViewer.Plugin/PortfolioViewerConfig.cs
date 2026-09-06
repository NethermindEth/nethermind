// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.PortfolioViewer.Plugin;

/// <inheritdoc cref="IPortfolioViewerConfig"/>
public class PortfolioViewerConfig : IPortfolioViewerConfig
{
    /// <inheritdoc/>
    public bool Enabled { get; set; } = false;

    /// <inheritdoc/>
    public string SiblingProbePorts { get; set; } = "8545,8546,8547,8548,8549,8550";
}
