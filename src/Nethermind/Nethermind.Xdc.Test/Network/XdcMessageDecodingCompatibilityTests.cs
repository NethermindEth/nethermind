// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network;
using Nethermind.Network.Test.P2P;
using Nethermind.Xdc.P2P.Messages;
using Nethermind.Xdc.Test.Helpers;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.MessageDecodingCompatibility;

namespace Nethermind.Xdc.Test.Network;

/// <summary>
/// The XDC counterpart of <see cref="MessageDecodingCompatibilityTests"/>, for the serializers <see cref="XdcModule"/> registers.
/// </summary>
[TestFixture]
public class XdcMessageDecodingCompatibilityTests
{
    private XdcTestBlockchain _chain = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => _chain = await XdcTestBlockchain.Create(0);

    [OneTimeTearDown]
    public void OneTimeTearDown() => _chain.Dispose();

    private static IEnumerable<Sample> Samples()
    {
        yield return Sample.Of(static () => new XdcGetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 }));
    }

    [Test]
    public void Every_registered_span_serializer_has_a_sample() =>
        AssertSampled(_chain.Container.Resolve<IReadOnlyList<SerializerInfo>>(), [.. MessageDecodingCompatibilityTests.Samples(), .. Samples()]);

    [Test]
    public void Accepts_a_trailing_byte_only_where_the_handlers_always_did([ValueSource(nameof(Samples))] Sample sample, [Values] bool trailingByte) =>
        AssertTrailingByteHandling(_chain.Container.Resolve<IMessageSerializationService>(), sample, trailingByte);
}
