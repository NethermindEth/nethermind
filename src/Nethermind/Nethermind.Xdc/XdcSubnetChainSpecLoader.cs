// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Xdc;

public class XdcSubnetChainSpecLoader : XdcChainSpecLoader
{
    protected override BlockHeader CreateGenesisHeader(BlockHeader header) => XdcSubnetBlockHeader.FromBlockHeader(header);
}
