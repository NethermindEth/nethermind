// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Optimism;

public interface IOPConfigHelper
{
    Address L1FeeReceiver { get; }

    bool IsBedrock(BlockHeader header);
    bool IsRegolith(BlockHeader header);
    bool IsCanyon(BlockHeader header);
    Address Create2DeployerAddress { get; }
    Hash256 Create2DeployerCodeHash { get; }
    byte[] Create2DeployerCode { get; }
}
