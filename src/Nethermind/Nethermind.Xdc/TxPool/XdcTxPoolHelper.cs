// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.TxPool;

internal static class XdcTxPoolHelper
{
    public static (XdcBlockHeader, long, IXdcReleaseSpec) GetSpecAndHeader(
        IBlockTree blockTree,
        ISpecProvider specProvider)
    {
        XdcBlockHeader header = (XdcBlockHeader)blockTree.Head!.Header;
        long currentHeaderNumber = header.Number + 1;
        IXdcReleaseSpec xdcSpec = specProvider.GetXdcSpec(currentHeaderNumber);

        return (header, currentHeaderNumber, xdcSpec);
    }
}
