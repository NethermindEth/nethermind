// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle;

/// <remarks>
/// The HardforkLabelsGenerator is not wired into the ZisK guest build (the guest builds its
/// spec from an embedded chain_config and never enumerates the label registry), so the
/// generated partial impl is absent there — provide a stub. Mainline keeps the source-generated
/// partial via the .std/non-suffixed file.
/// </remarks>
public static partial class HardforkLabels
{
    private static partial IReadOnlyList<IHardforkLabel> BuildAll() => System.Array.Empty<IHardforkLabel>();
}
