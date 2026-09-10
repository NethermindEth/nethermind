// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
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
    private static readonly Hash256 SomeHash = new("0x73f947f215a884a09c953ffd171e3a3feab564dd67cfbcbd5ee321143a220533");
    private static readonly Address DepositAddress = TestItem.AddressA;
    private static readonly Address SomeAddressA = TestItem.AddressB;
    private static readonly Address SomeAddressB = TestItem.AddressC;
    private static readonly Address SomeAddressC = TestItem.AddressD;
    private static readonly Address SomeAddressD = TestItem.AddressE;
    private static readonly Hash256 SourceHashLogIndex0 = new("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501");
    private static readonly Hash256 SourceHashLogIndex1 = new("0xe0afd0f8dec64b119c51723546cd6ff231b37aed016d7a2934eb6caf5d40eae2");

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(0));
    }

    [Test]
    public void DeriveUserDeposits_NullLogs()
    {
        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.EIP1559,
                Status = 1,
                Logs = null,
                BlockHash = SomeHash,
            },
        ];
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(0));
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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(0));
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
        Assert.That(build, Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void DeriveUserDeposits_FailedDeposit()
    {
        Hash256 blockHash = SomeHash;
        Address from = SomeAddressA;
        Address to = SomeAddressB;

        DepositLogEventV0 depositLogEventV0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        byte[] logData = depositLogEventV0.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(0));
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDeposit()
    {
        Hash256 blockHash = SomeHash;
        Address from = SomeAddressA;
        Address to = SomeAddressB;

        DepositLogEventV0 depositLogEventV0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        byte[] logData = depositLogEventV0.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Transaction expectedTransaction = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(to)
            .WithValue(depositLogEventV0.Value)
            .WithGasLimit(depositLogEventV0.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0.Data.ToArray())
            .TestObject;

        Assert.That(depositTransactions.Length, Is.EqualTo(1));
        Assert.That(depositTransactions[0], Is.EqualTo(expectedTransaction).UsingTransactionComparer());
    }

    [Test]
    public void DeriveUserDeposits_IsCreation()
    {
        Hash256 blockHash = SomeHash;
        Address from = SomeAddressA;

        DepositLogEventV0 depositLogEventV0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = true,
        };
        byte[] logData = depositLogEventV0.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Transaction expectedTransaction = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(null)
            .WithValue(depositLogEventV0.Value)
            .WithGasLimit(depositLogEventV0.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0.Data.ToArray())
            .TestObject;

        Assert.That(depositTransactions.Length, Is.EqualTo(1));

        Assert.That(depositTransactions[0], Is.EqualTo(expectedTransaction).UsingTransactionComparer());
    }

    [Test]
    public void DeriveUserDeposits_MultipleReceiptsMixedStatus()
    {
        Hash256 blockHash = SomeHash;
        Address from = SomeAddressA;
        Address to = SomeAddressB;

        DepositLogEventV0 depositLogEventV0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        byte[] logData = depositLogEventV0.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Transaction expectedTransaction = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(to)
            .WithValue(depositLogEventV0.Value)
            .WithGasLimit(depositLogEventV0.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0.Data.ToArray())
            .TestObject;

        Assert.That(depositTransactions.Length, Is.EqualTo(1));

        Assert.That(depositTransactions[0], Is.EqualTo(expectedTransaction).UsingTransactionComparer());
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDepositMultipleLogs()
    {
        Hash256 blockHash = SomeHash;
        Address from = SomeAddressA;
        Address to = SomeAddressB;

        DepositLogEventV0 depositLogEventV0_0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        byte[] logData_0 = depositLogEventV0_0.ToBytes();

        DepositLogEventV0 depositLogEventV0_1 = new()
        {
            Data = Bytes.FromHexString("0xe19ea336343e12e35237bb667fd0336a4fd9"),
            Mint = 0,
            Value = UInt256.Parse("14659767778871345152"),
            Gas = 8078654,
            IsCreation = true,
        };
        byte[] logData_1 = depositLogEventV0_1.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Transaction expectedTransaction_0 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(to)
            .WithValue(depositLogEventV0_0.Value)
            .WithGasLimit(depositLogEventV0_0.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_0.Data.ToArray())
            .TestObject;

        Transaction expectedTransaction_1 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from)
            .WithTo(null)
            .WithValue(depositLogEventV0_1.Value)
            .WithGasLimit(depositLogEventV0_1.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xe0afd0f8dec64b119c51723546cd6ff231b37aed016d7a2934eb6caf5d40eae2"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_1.Data.ToArray())
            .TestObject;

        Assert.That(depositTransactions.Length, Is.EqualTo(2));
        Assert.That(depositTransactions[0], Is.EqualTo(expectedTransaction_0).UsingTransactionComparer());

        Assert.That(depositTransactions[1], Is.EqualTo(expectedTransaction_1).UsingTransactionComparer());
    }

    [Test]
    public void DeriveUserDeposits_FailedDepositMultipleLogs()
    {
        Hash256 blockHash = SomeHash;
        Address from = SomeAddressA;
        Address to = SomeAddressB;

        DepositLogEventV0 depositLogEventV0_0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        byte[] logData_0 = depositLogEventV0_0.ToBytes();

        DepositLogEventV0 depositLogEventV0_1 = new()
        {
            Data = Bytes.FromHexString("0xe19ea336343e12e35237bb667fd0336a4fd9"),
            Mint = 0,
            Value = UInt256.Parse("14659767778871345152"),
            Gas = 8078654,
            IsCreation = true,
        };
        byte[] logData_1 = depositLogEventV0_1.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(0));
    }

    [Test]
    public void DeriveUserDeposits_SuccessfulDepositNotAllDepositLogs()
    {
        Hash256 blockHash = SomeHash;
        Address from_0 = SomeAddressA;
        Address to_0 = SomeAddressB;

        Address from_1 = SomeAddressC;
        Address to_1 = SomeAddressD;

        DepositLogEventV0 depositLogEventV0_0 = new()
        {
            Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
            Mint = 0,
            Value = UInt256.Parse("195000000000000000000"),
            Gas = 8732577,
            IsCreation = false,
        };
        byte[] logData_0 = depositLogEventV0_0.ToBytes();

        DepositLogEventV0 depositLogEventV0_1 = new()
        {
            Data = Bytes.FromHexString("0xe19ea336343e12e35237bb667fd0336a4fd9"),
            Mint = 0,
            Value = UInt256.Parse("14659767778871345152"),
            Gas = 8078654,
            IsCreation = false,
        };
        byte[] logData_1 = depositLogEventV0_1.ToBytes();

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
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Transaction expectedTransaction_0 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from_0)
            .WithTo(to_0)
            .WithValue(depositLogEventV0_0.Value)
            .WithGasLimit(depositLogEventV0_0.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xa39c0336f8bb13bdeb6cb1a969ee335af770f40048fed5064c1f3becf19ca501"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_0.Data.ToArray())
            .TestObject;

        Transaction expectedTransaction_1 = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSenderAddress(from_1)
            .WithTo(to_1)
            .WithValue(depositLogEventV0_1.Value)
            .WithGasLimit(depositLogEventV0_1.Gas)
            .WithGasPrice(0)
            .WithMaxPriorityFeePerGas(0)
            .WithMaxFeePerGas(0)
            .WithSourceHash(new Hash256("0xf3a97e2bed2ee2a61cfadad45283592d1674fa5647392e97be02e404f1a15e52"))
            .WithIsOPSystemTransaction(false)
            .WithData(depositLogEventV0_1.Data.ToArray())
            .TestObject;

        Assert.That(depositTransactions.Length, Is.EqualTo(2));
        Assert.That(depositTransactions[0], Is.EqualTo(expectedTransaction_0).UsingTransactionComparer());

        Assert.That(depositTransactions[1], Is.EqualTo(expectedTransaction_1).UsingTransactionComparer());
    }

    /// <summary>EIP-8141: the receipt status is the aggregate over the frames, so a deposit is credited exactly
    /// when the frame that emitted it succeeded, whatever the other frames or the receipt status say.</summary>
    [TestCase(TxFrameReceipt.StatusSuccess, TxFrameReceipt.StatusFailure, 0L, 1, TestName = "DeriveUserDeposits_FrameTx_CommittedDepositSurvivesUnrelatedFrameRevert")]
    [TestCase(TxFrameReceipt.StatusFailure, TxFrameReceipt.StatusSuccess, 0L, 0, TestName = "DeriveUserDeposits_FrameTx_RevertedFrameDepositIsNotCredited")]
    [TestCase(TxFrameReceipt.StatusFailure, TxFrameReceipt.StatusSuccess, 1L, 0, TestName = "DeriveUserDeposits_FrameTx_RevertedFrameDepositIsNotCreditedWhenStatusContradictsFrames")]
    [TestCase(TxFrameReceipt.StatusSkipped, TxFrameReceipt.StatusSuccess, 0L, 0, TestName = "DeriveUserDeposits_FrameTx_SkippedFrameDepositIsNotCredited")]
    public void DeriveUserDeposits_FrameTx_CreditsByEmittingFrameStatus(byte depositFrameStatus, byte otherFrameStatus, long receiptStatus, int expectedDeposits)
    {
        DepositLogEventV0 depositEvent = SomeDepositEvent();
        LogEntryForRpc depositLog = DepositLog(SomeAddressA, SomeAddressB, depositEvent.ToBytes(), 0);

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.FrameTx,
                Status = receiptStatus,
                Logs = [depositLog],
                FrameReceipts =
                [
                    new FrameReceiptForRpc { Status = depositFrameStatus, Logs = [depositLog.ToLogEntry()] },
                    new FrameReceiptForRpc { Status = otherFrameStatus, Logs = [] },
                ],
                BlockHash = SomeHash,
            },
        ];
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(expectedDeposits));
        if (expectedDeposits == 1)
        {
            Assert.That(depositTransactions[0], Is.EqualTo(ExpectedDeposit(SomeAddressA, SomeAddressB, depositEvent, SourceHashLogIndex0)).UsingTransactionComparer());
        }
    }

    [Test]
    public void DeriveUserDeposits_FrameTx_AttributesLogsInFrameOrder()
    {
        DepositLogEventV0 depositEvent = SomeDepositEvent();
        LogEntryForRpc revertedLog = DepositLog(SomeAddressA, SomeAddressB, depositEvent.ToBytes(), 0);
        LogEntryForRpc committedLog = DepositLog(SomeAddressC, SomeAddressD, depositEvent.ToBytes(), 1);

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.FrameTx,
                Status = 0,
                Logs = [revertedLog, committedLog],
                FrameReceipts =
                [
                    new FrameReceiptForRpc { Status = TxFrameReceipt.StatusFailure, Logs = [revertedLog.ToLogEntry()] },
                    new FrameReceiptForRpc { Status = TxFrameReceipt.StatusSuccess, Logs = [committedLog.ToLogEntry()] },
                ],
                BlockHash = SomeHash,
            },
        ];
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(1));
        Assert.That(depositTransactions[0], Is.EqualTo(ExpectedDeposit(SomeAddressC, SomeAddressD, depositEvent, SourceHashLogIndex1)).UsingTransactionComparer());
    }

    /// <summary>The frame log counts can add up while a successful frame's run covers a log another frame
    /// emitted, so the runs are matched against the logs themselves before any of them credits a deposit.</summary>
    [Test]
    public void DeriveUserDeposits_FrameTx_MisplacedFrameLogsAreUnattributable()
    {
        DepositLogEventV0 depositEvent = SomeDepositEvent();
        LogEntryForRpc depositLog = DepositLog(SomeAddressA, SomeAddressB, depositEvent.ToBytes(), 0);
        LogEntryForRpc unrelatedLog = new()
        {
            Address = SomeAddressC,
            Topics = [SomeHash],
            Data = [],
            LogIndex = 1,
            BlockHash = SomeHash,
        };

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.FrameTx,
                Status = 0,
                Logs = [depositLog, unrelatedLog],
                FrameReceipts =
                [
                    new FrameReceiptForRpc { Status = TxFrameReceipt.StatusSuccess, Logs = [unrelatedLog.ToLogEntry()] },
                    new FrameReceiptForRpc { Status = TxFrameReceipt.StatusFailure, Logs = [depositLog.ToLogEntry()] },
                ],
                BlockHash = SomeHash,
            },
        ];
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions, Is.Empty);
    }

    /// <summary>Each field the attribution compares has to be able to reject a frame log on its own: the
    /// credited log is the one that is decoded, so a mismatched <c>Data</c> would carry the wrong mint.</summary>
    [TestCaseSource(nameof(FrameLogMismatchCases))]
    public void DeriveUserDeposits_FrameTx_MismatchedFrameLogIsUnattributable(LogEntry frameLog)
    {
        LogEntryForRpc depositLog = DepositLog(SomeAddressA, SomeAddressB, SomeDepositEvent().ToBytes(), 0);

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.FrameTx,
                Status = 0,
                Logs = [depositLog],
                FrameReceipts = [new FrameReceiptForRpc { Status = TxFrameReceipt.StatusSuccess, Logs = [frameLog] }],
                BlockHash = SomeHash,
            },
        ];
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions, Is.Empty);
    }

    /// <summary>Frames that do not account for exactly the receipt's logs cannot be attributed, so the
    /// aggregate status keeps gating.</summary>
    [TestCase(0L, 0)]
    [TestCase(1L, 1)]
    public void DeriveUserDeposits_FrameTx_UnattributableFramesFallBackToAggregateStatus(long receiptStatus, int expectedDeposits)
    {
        DepositLogEventV0 depositEvent = SomeDepositEvent();
        LogEntryForRpc depositLog = DepositLog(SomeAddressA, SomeAddressB, depositEvent.ToBytes(), 0);

        ReceiptForRpc[] receipts =
        [
            new()
            {
                Type = TxType.FrameTx,
                Status = receiptStatus,
                Logs = [depositLog],
                FrameReceipts = [new FrameReceiptForRpc { Status = TxFrameReceipt.StatusSuccess, Logs = [] }],
                BlockHash = SomeHash,
            },
        ];
        Transaction[] depositTransactions = _builder.BuildUserDepositTransactions(receipts).ToArray();

        Assert.That(depositTransactions.Length, Is.EqualTo(expectedDeposits));
    }

    /// <summary>The element annotation does not bind the deserializer, so an L1 endpoint can send
    /// <c>"logs": [null]</c>.</summary>
    [Test]
    public void DeriveUserDeposits_NullLogEntryIsRejected()
    {
        ReceiptForRpc[] receipts =
        [
            new()
            {
                Status = 1,
                Logs = [null!],
                BlockHash = SomeHash,
            },
        ];

        Assert.That(() => _builder.BuildUserDepositTransactions(receipts), Throws.TypeOf<ArgumentException>());
    }

    /// <summary>A frame log matching the receipt's deposit log in every field but one.</summary>
    private static TestCaseData[] FrameLogMismatchCases()
    {
        LogEntry depositLog = DepositLog(SomeAddressA, SomeAddressB, SomeDepositEvent().ToBytes(), 0).ToLogEntry();
        DepositLogEventV0 otherEvent = SomeDepositEvent() with { Value = 1 };

        return
        [
            new TestCaseData(new LogEntry(SomeAddressC, depositLog.Data, depositLog.Topics)) { TestName = "DeriveUserDeposits_FrameTx_MismatchedFrameLogIsUnattributable(Address)" },
            new TestCaseData(new LogEntry(depositLog.Address, depositLog.Data, [DepositEvent.ABIHash])) { TestName = "DeriveUserDeposits_FrameTx_MismatchedFrameLogIsUnattributable(TopicCount)" },
            new TestCaseData(new LogEntry(depositLog.Address, depositLog.Data, [.. depositLog.Topics[..3], SomeHash])) { TestName = "DeriveUserDeposits_FrameTx_MismatchedFrameLogIsUnattributable(Topic)" },
            new TestCaseData(new LogEntry(depositLog.Address, otherEvent.ToBytes(), depositLog.Topics)) { TestName = "DeriveUserDeposits_FrameTx_MismatchedFrameLogIsUnattributable(Data)" },
        ];
    }

    private static DepositLogEventV0 SomeDepositEvent() => new()
    {
        Data = Bytes.FromHexString("0x3444f4d68305342838072b3c49df1b64c60a"),
        Mint = 0,
        Value = UInt256.Parse("195000000000000000000"),
        Gas = 8732577,
        IsCreation = false,
    };

    private static LogEntryForRpc DepositLog(Address from, Address to, byte[] logData, long logIndex) => new()
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
        LogIndex = logIndex,
        BlockHash = SomeHash,
    };

    private static Transaction ExpectedDeposit(Address from, Address to, in DepositLogEventV0 depositEvent, Hash256 sourceHash) => Build.A.Transaction
        .WithType(TxType.DepositTx)
        .WithSenderAddress(from)
        .WithTo(to)
        .WithValue(depositEvent.Value)
        .WithGasLimit(depositEvent.Gas)
        .WithGasPrice(0)
        .WithMaxPriorityFeePerGas(0)
        .WithMaxFeePerGas(0)
        .WithSourceHash(sourceHash)
        .WithIsOPSystemTransaction(false)
        .WithData(depositEvent.Data.ToArray())
        .TestObject;
}
