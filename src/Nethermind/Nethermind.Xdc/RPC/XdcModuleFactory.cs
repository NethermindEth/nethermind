// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Modules;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.RPC;

internal class XdcModuleFactory : ModuleFactoryBase<IXdcRpcModule>
{
    private readonly IBlockTree _blockTree;
    private readonly ISnapshotManager _snapshotManager;
    private readonly ISpecProvider _specProvider;
    private readonly IQuorumCertificateManager _quorumCertificateManager;
    private readonly IEpochSwitchManager _epochSwitchManager;

    public XdcModuleFactory(
        IBlockTree blockTree,
        ISnapshotManager snapshotManager,
        ISpecProvider specProvider,
        IQuorumCertificateManager quorumCertificateManager,
        IEpochSwitchManager epochSwitchManager)
    {
        _blockTree = blockTree;
        _snapshotManager = snapshotManager;
        _specProvider = specProvider;
        _quorumCertificateManager = quorumCertificateManager;
        _epochSwitchManager = epochSwitchManager;
    }

    public override IXdcRpcModule Create()
    {
        return new XdcRpcModule(
            _blockTree,
            _snapshotManager,
            _specProvider,
            _quorumCertificateManager,
            _epochSwitchManager);
    }
}
