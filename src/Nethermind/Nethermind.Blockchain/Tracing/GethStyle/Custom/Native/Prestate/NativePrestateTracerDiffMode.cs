// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;

public class NativePrestateTracerDiffMode
{
    public Dictionary<Box<Address>, NativePrestateTracerAccount> pre { get; init; }
    public Dictionary<Box<Address>, NativePrestateTracerAccount> post { get; init; }
}
