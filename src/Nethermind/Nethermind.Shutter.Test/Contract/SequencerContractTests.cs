// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using Nethermind.Shutter.Contracts;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Shutter.Test.Contract;

[TestFixture]
public class SequencerContractTests
{
    // [TestCase("0x6F95B5D0AEF6FD2B28467215138C799506815F8260B25342CD43EBD369594184000000000000000000000000FA606B7EE5CB3BB4580FBAA304FE245F917994180000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000520800000000000000000000000000000000000000000000000000000000000001010386C2E4A79924304F366D1E337C7C7B3E24B46CC49EE9A9E6D4AE318422C59758375171F4422EC9049193F8E968BBD9E0177AF80BECB536075EEE87309E5BC2B90893A4D722D2C7759EF6E1F1F9E10495E588E952AFC3109FCDE1766CF7B3404070D50EC4692A3D06FAEC34756B3C26FBB35B6CFF4E1C43251D033456ED95BAAE03C0DED2032D025A9B725E6424587EE1AB043DF3C2E05F155D0F8030F6BD1D7C4971B8B17F2047F3FA5F72FB20D4C78A4FC5577B25EA0224FAAE51E99904C085E98BDCD18C66CAFC54615E50AD9F6B2EA60F4EE770ED1C88BB1D4C4A31CB381205F2CA4AB46AB961C84D5C49B6B4EE257EE3C25DFAB9242F1A3EE1E58E64BDB800000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0xf0a057cedc9717f5e05fe8844b111647091f02607da901a26bd8e5d580473fc9000000000000000000000000ac0c0b61aee9955d72870c98d8676b71cf0a867000000000000000000000000000000000000000000000000000000000000000800000000000000000000000000000000000000000000000000000000000005208000000000000000000000000000000000000000000000000000000000000010103a6ebbcc172f6a6bbc7cbca785299a7d7e0ed1892d7f098da3afe409878ad7d32db0a14b7635c15f95c8a9d1e7a744f4a16a65c2783b695d91ceb7a51ba00632fd083a0b100a0c9d9ce8025f7ed3023932a1d91efeed61a7f32f0441028a67fb60d4931cad7ebba57552b62f382e730916efe21cdf92a3a7524042c2ef9c3abf58e8e53a55d1df282b83bdf68fb33baf1264303e5a0f3dedad181868950c8e926e85fe0ce897397dfa5c607fd87b6cac7f1feaaee6a44de1b1e63af1a9a6820b6e7d67e9cc34c428549ad0aede45963d8fe03fec7c31d5041ff23226a928a08c80d8004bd82d4404ea6edab5fda342b35a27f46b44ee5623bb93830fbd93b53d900000000000000000000000000000000000000000000000000000000000000")]
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
                [
                    new("0xa7f1b5467be46c45249fb93063cceef96c63ddad03819246bc7770e32d4f5b7d"),
                    new("0x0000000000000000000000000000000000000000000000000000000000000001"),
                    new("0x0000000000000000000000000000000000000000000000000000000000000025")
                ]));

        // Assert
        Assert.NotNull(parsedTransaction);
    }
}