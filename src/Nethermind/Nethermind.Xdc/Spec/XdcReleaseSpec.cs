// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Spec;
internal class XdcReleaseSpec : ReleaseSpec, IXdcReleaseSpec
{
    public bool IsV2Enabled { get; set; }

}
