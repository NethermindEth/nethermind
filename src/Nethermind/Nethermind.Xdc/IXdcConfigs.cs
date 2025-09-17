// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public interface IXdcSubConfig
{
    public int MaxMasternodes { get; set; }
    public int MaxProtectorNodes { get; set; }
    public int MaxObserverNodes { get; set; }
    public ulong SwitchRound { get; set; }
    public int MinePeriod { get; set; }
    public int TimeoutSyncThreshold { get; set; }
    public int TimeoutPeriod { get; set; }
    public double CertThreshold { get; set; }
    public double MasternodeReward { get; set; }
    public double ProtectorReward { get; set; }
    public double ObserverReward { get; set; }
    public int MinimumMinerBlockPerEpoch { get; set; }
    public int LimitPenaltyEpoch { get; set; }
    public int MinimumSigningTx { get; set; }
    public ExpTimeoutConfig ExpTimeoutConfig { get; set; }
}
