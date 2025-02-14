// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
using NUnit.Framework;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.Test.CL;

[TestFixture]
public class DepositTransactionBuilderTest
{
    private static readonly Address DepositAddress = TestItem.AddressA;
    private static readonly Address SomeAddressA = TestItem.AddressB;
    private static readonly Address SomeAddressB = TestItem.AddressC;

    private readonly DepositTransactionBuilder _builder;

    public DepositTransactionBuilderTest()
    {
        var parameters = new CLChainSpecEngineParameters();
        _builder = new DepositTransactionBuilder(TestBlockchainIds.ChainId, parameters);
    }

    [Test]
    public void DeriveUserDeposits_NoDeposits()
    {
        List<ReceiptForRpc> receipts = [];
        List<Transaction> depositTransactions = _builder.BuildUserDepositTransactions(DepositAddress, receipts);

        depositTransactions.Count.Should().Be(0);
    }

    [Test]
    public void DeriveUserDeposits_OtherLog()
    {
        var blockHash = TestItem.KeccakA;

        List<ReceiptForRpc> receipts =
        [
            new ReceiptForRpc {
                Type = TxType.EIP1559,
                Status = 1,
                LogsBloom = Bloom.Empty,
                Logs = [
                    new LogEntryForRpc() {
                        Address = SomeAddressA,
                    }
                ],
                TransactionHash = Hash256.Zero,
                ContractAddress = Address.Zero,
                BlockHash = blockHash,
            },
        ];
        List<Transaction> depositTransactions = _builder.BuildUserDepositTransactions(DepositAddress, receipts);

        depositTransactions.Count.Should().Be(0);
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDeposit()
    {
        /*
            See: https://github.com/ethereum-optimism/optimism/blob/ca4b1f687977d3f771d6e3a1b8c6f113f2331f63/packages/contracts-bedrock/src/L1/OptimismPortal2.sol#L144

            /// @notice Emitted when a transaction is deposited from L1 to L2.
            ///         The parameters of this event are read by the rollup node and used to derive deposit
            ///         transactions on L2.
            /// @param from       Address that triggered the deposit transaction.
            /// @param to         Address that the deposit transaction is directed to.
            /// @param version    Version of this deposit transaction event.
            /// @param opaqueData ABI encoded deposit data to be parsed off-chain.
            event TransactionDeposited(address indexed from, address indexed to, uint256 indexed version, bytes opaqueData);
         */

        var blockHash = new Hash256("0x73f947f215a884a09c953ffd171e3a3feab564dd67cfbcbd5ee321143a220533");
        var from = SomeAddressA;
        var to = SomeAddressB;

        var depositLogEventV0 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        var logData = depositLogEventV0.ToBytes();

        List<ReceiptForRpc> receipts =
        [
            new ReceiptForRpc {
                Type = TxType.EIP1559,
                Status = 1,
                LogsBloom = Bloom.Empty,
                Logs = [
                    new LogEntryForRpc {
                        Address = DepositAddress,
                        Topics = [
                            DepositEvent.ABIHash,
                            new Hash256(from.Bytes.PadLeft(32)),
                            new Hash256(to.Bytes.PadLeft(32)),
                            DepositEvent.Version0,
                        ],
                        Data = logData,
                        LogIndex = 0,
                        BlockHash = blockHash,
                    }
                ],
                TransactionHash = Hash256.Zero,
                ContractAddress = Address.Zero,
                BlockHash = blockHash,
            },
        ];
        List<Transaction> depositTransactions = _builder.BuildUserDepositTransactions(DepositAddress, receipts);

        var expectedTransaction = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(to)
            .WithValue(depositLogEventV0.Value)
            .WithGasLimit((long)depositLogEventV0.Gas) // WARNING: dangerous cast
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0.Data.ToArray())
            .TestObject;

        depositTransactions.Count.Should().Be(1);
        // NOTE: Check if we can simplify this assertion
        depositTransactions[0].Should().BeEquivalentTo(expectedTransaction, config => config.Excluding(x => x.Data));
        depositTransactions[0].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction.Data?.ToArray());
    }
}
