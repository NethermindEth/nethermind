// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal interface IMasternodesManager
{
    void UpdateMasterNodes(XdcBlockHeader header, Address[] ms);
    Address[] GetMasternodes(XdcBlockHeader header);
    bool TryCalcMasterNodes(XdcBlockHeader header, ulong currentRound, out Address[] masterNodes, out Address[] penalties);
}
