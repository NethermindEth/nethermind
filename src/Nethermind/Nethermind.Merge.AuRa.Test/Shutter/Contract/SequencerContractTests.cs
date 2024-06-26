// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test.Shutter.Contract;

public class SequencerContractTests
{
    [TestCase("0x6F95B5D0AEF6FD2B28467215138C799506815F8260B25342CD43EBD369594184000000000000000000000000FA606B7EE5CB3BB4580FBAA304FE245F917994180000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000520800000000000000000000000000000000000000000000000000000000000001010386C2E4A79924304F366D1E337C7C7B3E24B46CC49EE9A9E6D4AE318422C59758375171F4422EC9049193F8E968BBD9E0177AF80BECB536075EEE87309E5BC2B90893A4D722D2C7759EF6E1F1F9E10495E588E952AFC3109FCDE1766CF7B3404070D50EC4692A3D06FAEC34756B3C26FBB35B6CFF4E1C43251D033456ED95BAAE03C0DED2032D025A9B725E6424587EE1AB043DF3C2E05F155D0F8030F6BD1D7C4971B8B17F2047F3FA5F72FB20D4C78A4FC5577B25EA0224FAAE51E99904C085E98BDCD18C66CAFC54615E50AD9F6B2EA60F4EE770ED1C88BB1D4C4A31CB381205F2CA4AB46AB961C84D5C49B6B4EE257EE3C25DFAB9242F1A3EE1E58E64BDB800000000000000000000000000000000000000000000000000000000000000")]
    public void GetEvents(string hex)
    {
        // Arrange
        SequencerContract sequencerContract = new(TestItem.AddressA, Substitute.For<ILogFinder>(), LimboLogs.Instance);

        // Act
        ISequencerContract.TransactionSubmitted parsedTransaction = sequencerContract.ParseTransactionSubmitted(
            new FilterLog(
                0,
                0,
                0,
                TestItem.KeccakA,
                0,
                TestItem.KeccakB,
                TestItem.AddressA,
                Bytes.FromHexString(hex),
                []));

        // Assert
        Assert.NotNull(parsedTransaction);
    }
}
