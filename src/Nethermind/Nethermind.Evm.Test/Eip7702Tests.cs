// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;
using System.Collections.Generic;

namespace Nethermind.Evm.Test;
public class Eip7702Tests : VirtualMachineTestsBase
{
    protected override ForkActivation Activation => MainnetSpecProvider.PragueActivation;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

    protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };



}

