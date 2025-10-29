// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc;
public interface IEpochSwitchManager
{
    bool IsEpochSwitchAtRound(ulong currentRound, XdcBlockHeader parent);
    bool IsEpochSwitchAtBlock(XdcBlockHeader header);
    EpochSwitchInfo? GetEpochSwitchInfo(XdcBlockHeader? header);
    EpochSwitchInfo? GetEpochSwitchInfo(Hash256 blockHash);
    EpochSwitchInfo? GetTimeoutCertificateEpochInfo(TimeoutCertificate timeoutCertificate);
    BlockRoundInfo? GetBlockByEpochNumber(ulong epochNumber);
}
