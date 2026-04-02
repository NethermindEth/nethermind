// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.RPC;

internal class XdcModuleFactory(
        IBlockTree blockTree,
        ISnapshotManager snapshotManager,
        ISpecProvider specProvider,
        IQuorumCertificateManager quorumCertificateManager,
        IEpochSwitchManager epochSwitchManager,
        IVotesManager votesManager,
        ITimeoutCertificateManager timeoutCertificateManager,
        ISyncInfoManager syncInfoManager) : ModuleFactoryBase<IXdcRpcModule>
{
    public override IXdcRpcModule Create()
    {
        return new XdcRpcModule(
            blockTree,
            snapshotManager,
            specProvider,
            quorumCertificateManager,
            epochSwitchManager,
            votesManager,
            timeoutCertificateManager,
            syncInfoManager);
    }
}
