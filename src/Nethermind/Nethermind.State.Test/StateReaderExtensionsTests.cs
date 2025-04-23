\\\\\\// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Test
{
    [TestFixture]
    public class StateReaderExtensionsTests
    {
        private IStateReader _stateReader;
        private Hash256 _stateRoot;
        private Address _address;

        [SetUp]
        public void Setup()
        {
            _stateReader = Substitute.For<IStateReader>();
            _stateRoot = new Hash256("0x1234567890123456789012345678901234567890123456789012345678901234");
            _address = new Address("0x1234567890123456789012345678901234567890");
        }

        [Test]
        public void GetAccount_returns_existing_account()
        {
            // Arrange
            AccountStruct expectedAccountStruct = new AccountStruct(
                UInt256.One,
                UInt256.One * 2,
                new Hash256("0x2234567890123456789012345678901234567890123456789012345678901234"),
                new Hash256("0x3234567890123456789012345678901234567890123456789012345678901234"));

            _stateReader.TryGetAccount(_stateRoot, _address, out Arg.Any<AccountStruct>())
                .Returns(x => {
                    x[2] = expectedAccountStruct;
                    return true;
                });

            // Act
            Account account = StateReaderExtensions.GetAccount(_stateReader, _stateRoot, _address);

            // Assert
            Assert.That(account.Nonce, Is.EqualTo(expectedAccountStruct.Nonce));
            Assert.That(account.Balance, Is.EqualTo(expectedAccountStruct.Balance));
            Assert.That(account.StorageRoot, Is.EqualTo(expectedAccountStruct.StorageRoot));
            Assert.That(account.CodeHash, Is.EqualTo(expectedAccountStruct.CodeHash));
        }

        [Test]
        public void GetAccount_returns_empty_account_when_not_exists()
        {
            // Arrange
            _stateReader.TryGetAccount(_stateRoot, _address, out Arg.Any<AccountStruct>())
                .Returns(false);

            // Act
            Account account = StateReaderExtensions.GetAccount(_stateReader, _stateRoot, _address);

            // Assert
            Assert.That(account, Is.EqualTo(Account.TotallyEmpty));
        }
    }
}
