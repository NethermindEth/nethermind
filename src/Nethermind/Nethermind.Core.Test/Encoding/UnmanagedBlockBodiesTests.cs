// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class UnmanagedBlockBodiesTests
{
    [Test]
    public void Should_pass_roundtrip()
    {
        // TODO: Test that if there are memory owner, it should dispose them
    }
}
