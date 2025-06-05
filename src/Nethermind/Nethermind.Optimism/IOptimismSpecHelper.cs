// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism;

public interface IOptimismSpecHelper
{
    Address? L1FeeReceiver { get; }

    bool IsBedrock(BlockHeader header);
    bool IsRegolith(BlockHeader header);
    bool IsCanyon(BlockHeader header);
    bool IsEcotone(BlockHeader header);
    bool IsDelta(BlockHeader header);
    bool IsFjord(BlockHeader header);
    bool IsGranite(BlockHeader header);
    bool IsHolocene(BlockHeader header);
    bool IsIsthmus(BlockHeader header);
    Address? Create2DeployerAddress { get; }
    byte[]? Create2DeployerCode { get; }
}
