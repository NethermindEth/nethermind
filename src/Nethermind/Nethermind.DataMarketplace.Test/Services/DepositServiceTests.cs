// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.DataMarketplace.Test.Services
{
    public class DepositServiceTests : ContractInteractionTest
    {
        [SetUp]
        public async Task Setup()
        {
            await Prepare();
        }

        [Test]
        public async Task Can_make_and_verify_deposit_2()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Deposit deposit = new Deposit(new Keccak("0x7b1a21b95d2564c0e65807e921470575b20215d5430644014640009776d2fe04"), 336, 1549531335u, UInt256.Parse("33600000000000000000"));
            Address address = new Address("2b5ad5c4795c026514f8317c7a215e218dccd6cf");
            Keccak depositTxHash = await depositService.MakeDepositAsync(address, deposit, 20.GWei());
            _bridge.IncrementNonce(address);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");
            Assert.Greater(await depositService.VerifyDepositAsync(_consumerAccount, deposit.Id), 0, "deposit verified");
        }

        [Test]
        public async Task Can_make_and_verify_deposit()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Deposit deposit = new Deposit(Keccak.Compute("a secret"), 10, (uint)Timestamper.Default.UnixTime.SecondsLong + 86000, 1.Ether());
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            _bridge.IncrementNonce(_consumerAccount);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");
            Assert.Greater(await depositService.VerifyDepositAsync(_consumerAccount, deposit.Id), 0, "deposit verified");
        }

        [Test]
        public async Task Make_deposit_verify_incorrect_id()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Deposit deposit = new Deposit(Keccak.Compute("a secret"), 10, (uint)Timestamper.Default.UnixTime.SecondsLong + 86000, 1.Ether());
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");
            Assert.AreEqual(0U, await depositService.VerifyDepositAsync(_consumerAccount, Keccak.Compute("incorrect id")), "deposit verified");
        }

        [Test]
        public async Task Can_make_and_verify_deposit_locally()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Deposit deposit = new Deposit(Keccak.Compute("a secret"), 10, (uint)Timestamper.Default.UnixTime.SecondsLong + 86000, 1.Ether());
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            _bridge.IncrementNonce(_consumerAccount);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            Assert.AreEqual(StatusCode.Success, depositTxReceipt.StatusCode, $"deposit made {depositTxReceipt.Error} {Encoding.UTF8.GetString(depositTxReceipt.ReturnValue ?? new byte[0])}");
            Assert.True(await depositService.VerifyDepositAsync(_consumerAccount, deposit.Id) > 0, "deposit verified");
        }

        [Test]
        public void Throws_when_unexpected_contract_address()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Assert.ThrowsAsync<InvalidDataException>(async () => await depositService.ValidateContractAddressAsync(Address.Zero));
        }

        [Test]
        public void Throws_when_no_code_deployed()
        {
            IStateReader stateReader = Substitute.For<IStateReader>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(Build.A.Block.Genesis.TestObject);

            _ndmBridge = new NdmBlockchainBridge(
                Substitute.For<IBlockchainBridge>(),
                blockFinder,
                stateReader,
                Substitute.For<ITxSender>());
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Address contractAddress = new Address(_ndmConfig.ContractAddress);
            stateReader.GetCode(Arg.Any<Keccak>(), contractAddress).Returns(Array.Empty<byte>());
            Assert.ThrowsAsync<InvalidDataException>(async () => await depositService.ValidateContractAddressAsync(contractAddress));
        }

        [Test]
        public void Throws_when_unexpected_code()
        {
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(Build.A.Block.Genesis.TestObject);

            IStateReader stateReader = Substitute.For<IStateReader>();
            _ndmBridge = new NdmBlockchainBridge(
                Substitute.For<IBlockchainBridge>(),
                blockFinder,
                stateReader,
                Substitute.For<ITxSender>());

            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Address contractAddress = new Address(_ndmConfig.ContractAddress);
            stateReader.GetCode(Arg.Any<Keccak>(), contractAddress).Returns(Bytes.FromHexString("0xa234"));
            Assert.ThrowsAsync<InvalidDataException>(async () => await depositService.ValidateContractAddressAsync(contractAddress));
        }

        [Test]
        public async Task Ok_when_code_is_valid()
        {
            IStateReader stateReader = Substitute.For<IStateReader>();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.Head.Returns(Build.A.Block.Genesis.TestObject);

            _ndmBridge = new NdmBlockchainBridge(
                Substitute.For<IBlockchainBridge>(),
                blockFinder,
                stateReader,
                Substitute.For<ITxSender>());

            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Address contractAddress = new Address(_ndmConfig.ContractAddress);
            stateReader.GetCode(Arg.Any<Keccak>(), contractAddress).Returns(Bytes.FromHexString(ContractData.DeployedCode));
            await depositService.ValidateContractAddressAsync(contractAddress);
        }

        [Test]
        public async Task Returns_a_valid_balance()
        {
            DepositService depositService = new DepositService(_ndmBridge, _abiEncoder, _wallet, _contractAddress);
            Deposit deposit = new Deposit(Keccak.Compute("a secret"), 10, (uint)Timestamper.Default.UnixTime.SecondsLong + 86000, 1.Ether());
            Keccak depositTxHash = await depositService.MakeDepositAsync(_consumerAccount, deposit, 20.GWei());
            _bridge.IncrementNonce(_consumerAccount);
            TxReceipt depositTxReceipt = _bridge.GetReceipt(depositTxHash);
            UInt256 balance = await depositService.ReadDepositBalanceAsync(_consumerAccount, deposit.Id);
            Assert.AreEqual(balance, 1.Ether());
        }
    }
}
