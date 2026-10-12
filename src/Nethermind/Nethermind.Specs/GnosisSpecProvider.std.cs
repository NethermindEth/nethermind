// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs;

public partial class GnosisSpecProvider
{
    public static readonly Address FeeCollector = new("0x6BBe78ee9e474842Dbd4AB4987b3CeFE88426A92");

    public static GnosisSpecProvider Instance { get; } = new();
}
