// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc.Types;

internal class XdcPayloadAttributes : PayloadAttributes
{
    public ulong Round { get; set; }
    public QuorumCertificate? QuorumCertificate { get; set; }
}
