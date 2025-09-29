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
    EpochSwitchInfo? GetPreviousEpochSwitchInfoByHash(Hash256 parentHash, int limit);
    bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent, out ulong epochNumber);
    bool IsEpochSwitchAtBlock(XdcBlockHeader header, out ulong epochNumber);
    EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader header, Hash256 parentHash);
    bool IsEpochSwitch(XdcBlockHeader header, out ulong epochNumber);
    EpochSwitchInfo[] GetEpochSwitchBetween(XdcBlockHeader start, XdcBlockHeader end);
    (ulong currentCheckpointNumber, ulong epochNumber) GetCurrentEpochNumbers(ulong blockNumber);
    EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCert timeoutCert);
    BlockRoundInfo? GetBlockByEpochNumber(ulong epochNumber);
}
