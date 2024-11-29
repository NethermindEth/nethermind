// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public class SystemConfigDeriver : ISystemConfigDeriver
{
    public SystemConfig SystemConfigFromL2Payload(ExecutionPayload l2Payload)
    {
        if (l2Payload.Transactions.Length == 0)
        {
            throw new ArgumentException("No txs in payload");
        }
        Transaction depositTx = TxDecoder.Instance.Decode(l2Payload.Transactions[0]);
        if (depositTx.Type != TxType.DepositTx)
        {
            throw new ArgumentException("First tx is not deposit tx");
        }

        // TODO: all SystemConfig parameters should be encoded in tx.Data();
        throw new System.NotImplementedException();
    }

    public SystemConfig UpdateSystemConfigFromL1BLock(SystemConfig systemConfig)
    {
        throw new System.NotImplementedException();
    }
}
