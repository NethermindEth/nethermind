// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle;

public static partial class HardforkLabels
{
    // The source generator isn't wired into the ZisK guest build (the guest builds its spec from an
    // embedded chain_config and never enumerates the label registry), so the generated partial is
    // absent — stub it.
    private static partial IReadOnlyList<IHardforkLabel> BuildAll() => [];
}
