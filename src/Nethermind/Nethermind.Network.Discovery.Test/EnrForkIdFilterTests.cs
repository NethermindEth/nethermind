// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using NSubstitute;
using NUnit.Framework;
using EnrForkId = Nethermind.Network.Enr.ForkId;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class EnrForkIdFilterTests
{
    [Test]
    public void IsAcceptable_ShouldRejectRecordWithoutEthEntry()
    {
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        EnrForkIdFilter filter = CreateFilter(forkInfo);

        bool result = filter.IsAcceptable(new NodeRecord());

        Assert.That(result, Is.False);
        forkInfo.DidNotReceive().ValidateForkId(Arg.Any<NetworkForkId>(), Arg.Any<BlockHeader>());
    }

    [Test]
    public void IsAcceptable_ShouldValidateEthForkId()
    {
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.ValidateForkId(
            Arg.Is<NetworkForkId>(forkId =>
                forkId.ForkHash == 0x01020304 &&
                forkId.Next == 128),
            Arg.Any<BlockHeader>())
            .Returns(ValidationResult.Valid);
        EnrForkIdFilter filter = CreateFilter(forkInfo);

        bool result = filter.IsAcceptable(CreateRecord([1, 2, 3, 4], 128));

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsAcceptable_ShouldRejectIncompatibleEthForkId()
    {
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.ValidateForkId(Arg.Any<NetworkForkId>(), Arg.Any<BlockHeader>())
            .Returns(ValidationResult.IncompatibleOrStale);
        EnrForkIdFilter filter = CreateFilter(forkInfo);

        bool result = filter.IsAcceptable(CreateRecord([1, 2, 3, 4], 128));

        Assert.That(result, Is.False);
    }

    [Test]
    public void IsAcceptable_ShouldValidateNextBlockAboveSignedLongRange()
    {
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.ValidateForkId(
            Arg.Is<NetworkForkId>(forkId =>
                forkId.ForkHash == 0x01020304 &&
                forkId.Next == (ulong)long.MaxValue + 1),
            Arg.Any<BlockHeader>())
            .Returns(ValidationResult.Valid);
        EnrForkIdFilter filter = CreateFilter(forkInfo);

        bool result = filter.IsAcceptable(CreateRecord([1, 2, 3, 4], (ulong)long.MaxValue + 1));

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsAcceptable_ShouldRejectInvalidEthForkHash()
    {
        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        EnrForkIdFilter filter = CreateFilter(forkInfo);

        bool result = filter.IsAcceptable(CreateRecord([1, 2, 3], 128));

        Assert.That(result, Is.False);
        forkInfo.DidNotReceive().ValidateForkId(Arg.Any<NetworkForkId>(), Arg.Any<BlockHeader>());
    }

    private static EnrForkIdFilter CreateFilter(IForkInfo forkInfo)
        => new(Substitute.For<IBlockTree>(), forkInfo, LimboLogs.Instance);

    private static NodeRecord CreateRecord(byte[] forkHash, ulong nextBlock)
    {
        NodeRecord record = new();
        record.SetEntry(forkHash.Length == EnrForkId.ForkHashLength
            ? new EthEntry(forkHash, nextBlock)
            : new InvalidEthEntry(new EnrForkId(forkHash, nextBlock)));
        return record;
    }

    private sealed class InvalidEthEntry(EnrForkId value) : EnrContentEntry<EnrForkId>(value)
    {
        public override string Key => EnrContentKey.Eth;

        protected override int GetRlpLengthOfValue() => throw new NotSupportedException();

        protected override void EncodeValue<TWriter>(ref TWriter writer) => throw new NotSupportedException();
    }
}
