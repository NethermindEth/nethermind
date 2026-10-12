// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

// Lazy, so the guest runs no class constructor for this type.
public partial class MainnetSpecProvider
{
    public static ForkActivation ShanghaiActivation => (ParisBlockNumber + 1, ShanghaiBlockTimestamp);
    public static ForkActivation CancunActivation => (ParisBlockNumber + 2, CancunBlockTimestamp);
    public static ForkActivation PragueActivation => (ParisBlockNumber + 3, PragueBlockTimestamp);
    public static ForkActivation OsakaActivation => (ParisBlockNumber + 4, OsakaBlockTimestamp);
    public static ForkActivation BPO1Activation => (ParisBlockNumber + 5, BPO1BlockTimestamp);
    public static ForkActivation BPO2Activation => (ParisBlockNumber + 6, BPO2BlockTimestamp);
    public static ForkActivation AmsterdamActivation => (ParisBlockNumber + 7, AmsterdamBlockTimestamp);
    public static ForkActivation BogotaActivation => (ParisBlockNumber + 8, BogotaBlockTimestamp);

    public static MainnetSpecProvider Instance => field ??= new();
}
