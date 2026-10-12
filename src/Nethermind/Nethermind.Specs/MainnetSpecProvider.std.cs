// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public partial class MainnetSpecProvider
{
    public static ForkActivation ShanghaiActivation { get; } = (ParisBlockNumber + 1, ShanghaiBlockTimestamp);
    public static ForkActivation CancunActivation { get; } = (ParisBlockNumber + 2, CancunBlockTimestamp);
    public static ForkActivation PragueActivation { get; } = (ParisBlockNumber + 3, PragueBlockTimestamp);
    public static ForkActivation OsakaActivation { get; } = (ParisBlockNumber + 4, OsakaBlockTimestamp);
    public static ForkActivation BPO1Activation { get; } = (ParisBlockNumber + 5, BPO1BlockTimestamp);
    public static ForkActivation BPO2Activation { get; } = (ParisBlockNumber + 6, BPO2BlockTimestamp);
    public static ForkActivation AmsterdamActivation { get; } = (ParisBlockNumber + 7, AmsterdamBlockTimestamp);
    public static ForkActivation BogotaActivation { get; } = (ParisBlockNumber + 8, BogotaBlockTimestamp);

    public static MainnetSpecProvider Instance { get; } = new();
}
