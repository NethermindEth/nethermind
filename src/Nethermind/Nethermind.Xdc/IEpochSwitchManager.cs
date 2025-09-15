// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc.Types;
using Nethermind.Core.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public interface IEpochSwitchManager
{
    bool TryGetPreviousEpochSwitchInfoByHash(Hash256 parentHash, int limit, out EpochSwitchInfo epochInfo);
    bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent, out ulong epochNumber);
    bool IsEpochSwitchAtBlock(XdcBlockHeader header, out ulong epochNumber);
    bool TryGetEpochSwitchInfo(XdcBlockHeader header, Hash256 parentHash, out EpochSwitchInfo epochInfo);
    bool IsEpochSwitch(XdcBlockHeader header, out ulong epochNumber);
    bool TryGetEpochSwitchBetween(XdcBlockHeader start, XdcBlockHeader end, out List<EpochSwitchInfo> epochSwitchInfos);
    bool TryGetCurrentEpochSwitchBlock(ulong blockNumber, out ulong currentCheckpointNumber, out ulong epochNumber);
    bool TryGetTimeoutCertificateEpochInfo(TimeoutCert timeoutCert, out EpochSwitchInfo epochInfo);
}
