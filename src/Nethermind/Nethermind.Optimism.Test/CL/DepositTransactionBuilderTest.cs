// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Derivation;
using NUnit.Framework;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.Test.CL;

[TestFixture]
public class DepositTransactionBuilderTest
{
    private static readonly Hash256 SomeHash = new("0x73f947f215a884a09c953ffd171e3a3feab564dd67cfbcbd5ee321143a220533");
    private static readonly Address DepositAddress = TestItem.AddressA;
    private static readonly Address SomeAddressA = TestItem.AddressB;
    private static readonly Address SomeAddressB = TestItem.AddressC;
    private static readonly Address SomeAddressC = TestItem.AddressD;
    private static readonly Address SomeAddressD = TestItem.AddressE;

    private readonly CLChainSpecEngineParameters _engineParameters;
    private readonly DepositTransactionBuilder _builder;

    public DepositTransactionBuilderTest()
    {
        _engineParameters = new CLChainSpecEngineParameters { OptimismPortalProxy = DepositAddress };
        _builder = new DepositTransactionBuilder(TestBlockchainIds.ChainId, _engineParameters);
    }

    [Test]
    public void DeriveUserDeposits_NoDeposits()
    {
        ReceiptForRpc[] receipts = [];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        depositTransactions.Length.Should().Be(0);
    }

    [Test]
    public void DeriveUserDeposits_OtherLog()
    {
        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = SomeAddressA,
                    }
                ],
                BlockHash = SomeHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        depositTransactions.Length.Should().Be(0);
    }


    private static IEnumerable<LogEntryForRpc> InvalidLogFormatTestCases()
    {
        // Missing `from`, `to` and `version`
        yield return new LogEntryForRpc
        {
            Address = DepositAddress,
            Topics =
            [
                DepositEvent.ABIHash,
            ],
            Data = new byte[10],
            LogIndex = 0,
            BlockHash = SomeHash,
        };
        // Unknown version
        yield return new LogEntryForRpc
        {
            Address = DepositAddress,
            Topics =
            [
                DepositEvent.ABIHash,
                new Hash256(SomeAddressA.Bytes.PadLeft(32)),
                new Hash256(SomeAddressB.Bytes.PadLeft(32)),
                new Hash256("0x000000000000000000000000000000000000000000000000000000000000000f"),
            ],
            Data = new byte[10],
            LogIndex = 0,
            BlockHash = SomeHash,
        };
        // Missing address
        yield return new LogEntryForRpc
        {
            Address = DepositAddress,
            Topics =
            [
                DepositEvent.ABIHash,
                new Hash256(SomeAddressA.Bytes.PadLeft(32)),
                new Hash256("0x000000000000000000000000000000000000000000000000000000000000000f"),
            ],
            Data = new byte[10],
            LogIndex = 0,
            BlockHash = SomeHash,
        };
        // Invalid number of topics
        yield return new LogEntryForRpc
        {
            Address = DepositAddress,
            Topics =
            [
                DepositEvent.ABIHash,
                new Hash256(SomeAddressA.Bytes.PadLeft(32)),
                new Hash256(SomeAddressB.Bytes.PadLeft(32)),
                DepositEvent.Version0,
                Hash256.Zero,
            ],
            Data = new byte[10],
            LogIndex = 0,
            BlockHash = SomeHash,
        };
        // Invalid data
        yield return new LogEntryForRpc
        {
            Address = DepositAddress,
            Topics =
            [
                DepositEvent.ABIHash,
                new Hash256(SomeAddressA.Bytes.PadLeft(32)),
                new Hash256(SomeAddressB.Bytes.PadLeft(32)),
                DepositEvent.Version0,
            ],
            Data = new byte[33],
            LogIndex = 0,
            BlockHash = SomeHash,
        };
    }
    [TestCaseSource(nameof(InvalidLogFormatTestCases))]
    public void DeriveUserDeposits_ThrowsOnInvalidLogFormat(LogEntryForRpc log)
    {
        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs = [log],
                BlockHash = SomeHash,
            },
        ];
        Action build = () => _builder.BuildUserDepositTransactions(receipts).ToArray();
        build.Should().Throw<ArgumentException>();
    }

    [Test]
    public void DeriveUserDeposits_FailedDeposit()
    {
        var blockHash = SomeHash;
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

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 0, // Failed
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
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
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        depositTransactions.Length.Should().Be(0);
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDeposit()
    {
        var blockHash = SomeHash;
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

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
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
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

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

        depositTransactions.Length.Should().Be(1);
        // NOTE: Check if we can simplify this assertion
        depositTransactions[0].Should().BeEquivalentTo(expectedTransaction, config => config.Excluding(x => x.Data));
        depositTransactions[0].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction.Data?.ToArray());
    }

    [Test]
    public void DeriveUserDeposits_IsCreation()
    {
        var blockHash = SomeHash;
        var from = SomeAddressA;

        var depositLogEventV0 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = true,
        };
        var logData = depositLogEventV0.ToBytes();

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from.Bytes.PadLeft(32)),
                            Hash256.Zero,
                            DepositEvent.Version0,
                        ],
                        Data = logData,
                        LogIndex = 0,
                        BlockHash = blockHash,
                    }
                ],
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        var expectedTransaction = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(null)
            .WithValue(depositLogEventV0.Value)
            .WithGasLimit((long)depositLogEventV0.Gas) // WARNING: dangerous cast
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0.Data.ToArray())
            .TestObject;

        depositTransactions.Length.Should().Be(1);

        depositTransactions[0].Should().BeEquivalentTo(expectedTransaction, config => config.Excluding(x => x.Data));
        depositTransactions[0].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction.Data?.ToArray());
    }

    [Test]
    public void DeriveUserDeposits_MultipleReceiptsMixedStatus()
    {
        var blockHash = SomeHash;
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

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
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
                BlockHash = blockHash,
            },
            new()
            {
                Type = TxType.EIP1559,
                Status = 0, // Failed
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
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
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

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

        depositTransactions.Length.Should().Be(1);

        depositTransactions[0].Should().BeEquivalentTo(expectedTransaction, config => config.Excluding(x => x.Data));
        depositTransactions[0].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction.Data?.ToArray());
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDepositMultipleLogs()
    {
        var blockHash = SomeHash;
        var from = SomeAddressA;
        var to = SomeAddressB;

        var depositLogEventV0_0 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        var logData_0 = depositLogEventV0_0.ToBytes();

        var depositLogEventV0_1 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0xe19ea336343e12e35237bb667fd0336a4fd9"),
            Mint = 0,
            Value = UInt256.Parse("14659767778871345152"),
            Gas = 8078654,
            IsCreation = true,
        };
        var logData_1 = depositLogEventV0_1.ToBytes();

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from.Bytes.PadLeft(32)),
                            new Hash256(to.Bytes.PadLeft(32)),
                            DepositEvent.Version0,
                        ],
                        Data = logData_0,
                        LogIndex = 0,
                        BlockHash = blockHash,
                    },
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from.Bytes.PadLeft(32)),
                            Hash256.Zero,
                            DepositEvent.Version0,
                        ],
                        Data = logData_1,
                        LogIndex = 1,
                        BlockHash = blockHash,
                    },
                ],
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        var expectedTransaction_0 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(to)
            .WithValue(depositLogEventV0_0.Value)
            .WithGasLimit((long)depositLogEventV0_0.Gas) // WARNING: dangerous cast
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_0.Data.ToArray())
            .TestObject;

        var expectedTransaction_1 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(null)
            .WithValue(depositLogEventV0_1.Value)
            .WithGasLimit((long)depositLogEventV0_1.Gas) // WARNING: dangerous cast
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xe0afd0f8dec64b119c51723546cd6ff231b37aed016d7a2934eb6caf5d40eae2"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_1.Data.ToArray())
            .TestObject;

        depositTransactions.Length.Should().Be(2);
        depositTransactions[0].Should().BeEquivalentTo(expectedTransaction_0, config => config.Excluding(x => x.Data));
        depositTransactions[0].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction_0.Data?.ToArray());

        depositTransactions[1].Should().BeEquivalentTo(expectedTransaction_1, config => config.Excluding(x => x.Data));
        depositTransactions[1].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction_1.Data?.ToArray());
    }

    [Test]
    public void DeriveUserDeposits_FailedDepositMultipleLogs()
    {
        var blockHash = SomeHash;
        var from = SomeAddressA;
        var to = SomeAddressB;

        var depositLogEventV0_0 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        var logData_0 = depositLogEventV0_0.ToBytes();

        var depositLogEventV0_1 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0xe19ea336343e12e35237bb667fd0336a4fd9"),
            Mint = 0,
            Value = UInt256.Parse("14659767778871345152"),
            Gas = 8078654,
            IsCreation = true,
        };
        var logData_1 = depositLogEventV0_1.ToBytes();

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 0, // Failed
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from.Bytes.PadLeft(32)),
                            new Hash256(to.Bytes.PadLeft(32)),
                            DepositEvent.Version0,
                        ],
                        Data = logData_0,
                        LogIndex = 0,
                        BlockHash = blockHash,
                    },
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from.Bytes.PadLeft(32)),
                            Hash256.Zero,
                            DepositEvent.Version0,
                        ],
                        Data = logData_1,
                        LogIndex = 1,
                        BlockHash = blockHash,
                    },
                ],
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        depositTransactions.Length.Should().Be(0);
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDepositNotAllDepositLogs()
    {
        var blockHash = SomeHash;
        var from_0 = SomeAddressA;
        var to_0 = SomeAddressB;

        var from_1 = SomeAddressC;
        var to_1 = SomeAddressD;

        var depositLogEventV0_0 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        var logData_0 = depositLogEventV0_0.ToBytes();

        var depositLogEventV0_1 = new DepositLogEventV0
        {
            Data = Bytes.FromHexString("0xe19ea336343e12e35237bb667fd0336a4fd9"),
            Mint = 0,
            Value = UInt256.Parse("14659767778871345152"),
            Gas = 8078654,
            IsCreation = false,
        };
        var logData_1 = depositLogEventV0_1.ToBytes();

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs =
                [
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from_0.Bytes.PadLeft(32)),
                            new Hash256(to_0.Bytes.PadLeft(32)),
                            DepositEvent.Version0,
                        ],
                        Data = logData_0,
                        LogIndex = 0,
                        BlockHash = blockHash,
                    },
                    new LogEntryForRpc
                    {
                        Address = SomeAddressA,
                        Topics = [],
                        Data = new byte[10],
                        LogIndex = 1,
                        BlockHash = blockHash,
                    },
                    new LogEntryForRpc
                    {
                        Address = DepositAddress,
                        Topics =
                        [
                            DepositEvent.ABIHash,
                            new Hash256(from_1.Bytes.PadLeft(32)),
                            new Hash256(to_1.Bytes.PadLeft(32)),
                            DepositEvent.Version0,
                        ],
                        Data = logData_1,
                        LogIndex = 2,
                        BlockHash = blockHash,
                    },
                ],
                BlockHash = blockHash,
            },
        ];
        var depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        var expectedTransaction_0 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from_0)
            .WithTo(to_0)
            .WithValue(depositLogEventV0_0.Value)
            .WithGasLimit((long)depositLogEventV0_0.Gas) // WARNING: dangerous cast
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_0.Data.ToArray())
            .TestObject;

        var expectedTransaction_1 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from_1)
            .WithTo(to_1)
            .WithValue(depositLogEventV0_1.Value)
            .WithGasLimit((long)depositLogEventV0_1.Gas) // WARNING: dangerous cast
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xf3a97e2bed2ee2a61cfadad45283592d1674fa5647392e97be02e404f1a15e52"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_1.Data.ToArray())
            .TestObject;

        depositTransactions.Length.Should().Be(2);
        depositTransactions[0].Should().BeEquivalentTo(expectedTransaction_0, config => config.Excluding(x => x.Data));
        depositTransactions[0].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction_0.Data?.ToArray());

        depositTransactions[1].Should().BeEquivalentTo(expectedTransaction_1, config => config.Excluding(x => x.Data));
        depositTransactions[1].Data?.ToArray().Should().BeEquivalentTo(expectedTransaction_1.Data?.ToArray());
    }
}
