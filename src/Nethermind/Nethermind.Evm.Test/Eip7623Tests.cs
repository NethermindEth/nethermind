// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7623Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;

    [Test]
    public void non_zero_data_transaction_floor_cost_should_be_40()
    {
        var transaction = new Transaction { Data = new byte[] { 1 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(Standard: GasCostOf.Transaction + GasCostOf.TxDataNonZeroEip2028,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 4));
    }

    [Test]
    public void zero_data_transaction_floor_cost_should_be_10()
    {
        var transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(Standard: GasCostOf.Transaction + GasCostOf.TxDataZero,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623));
    }

    [Test]
    public void access_list_address_cost_includes_token_floor_with_eip7981()
    {
        // Address.Zero = 20 zero bytes = 20 tokens
        // access_list_cost = 2400 + 10 * 20 = 2600; floor = 21000 + 10 * 20 = 21200
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 20,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 20));
    }

    [Test]
    public void access_list_token_floor_not_applied_before_eip7981()
    {
        // Prague: EIP-7623 enabled, EIP-7981 NOT enabled → no token floor on access list
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Prague.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void access_list_with_storage_key_includes_all_tokens_with_eip7981()
    {
        // Address.Zero (20 zero bytes = 20 tokens) + UInt256.Zero (32 zero bytes = 32 tokens) = 52 tokens
        // access_list_cost = 2400 + 1900 + 10 * 52 = 4820; floor = 21000 + 10 * 52 = 21520
        AccessList accessList = new AccessList.Builder()
            .AddAddress(Address.Zero)
            .AddStorage(UInt256.Zero)
            .Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 52,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 52));
    }

    [Test]
    public void empty_transaction_standard_equals_floor()
    {
        // No data, no access list → 0 tokens; floor = standard = 21000
        var transaction = new Transaction { To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void contract_creation_no_data_standard_wins()
    {
        // To = null → TxCreate (+32000), no data → 0 tokens; standard = 53000, floor = 21000
        var transaction = new Transaction();
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxCreate,
            FloorGas: GasCostOf.Transaction));
    }

    [Test]
    public void contract_creation_with_calldata_standard_wins()
    {
        // 5 non-zero bytes; standard = 21000 + 32000 + 5*16 + EIP-3860 initcode(1 word * 2) = 53082
        // tokens = 5*4 = 20; floor = 21000 + 20*10 = 21200 → standard wins
        var transaction = new Transaction { Data = new byte[] { 1, 2, 3, 4, 5 } };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxCreate + 5 * GasCostOf.TxDataNonZeroEip2028 + GasCostOf.InitCodeWord,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * (5 * 4)));
    }

    [Test]
    public void mixed_calldata_counts_tokens_correctly()
    {
        // 3 zero bytes + 2 non-zero bytes; standard = 21000 + 3*4 + 2*16 = 21044
        // tokens = 3*1 + 2*4 = 11; floor = 21000 + 11*10 = 21110
        var transaction = new Transaction { Data = new byte[] { 0, 0, 0, 1, 2 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + 3 * GasCostOf.TxDataZero + 2 * GasCostOf.TxDataNonZeroEip2028,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 11));
    }

    [Test]
    public void zero_heavy_calldata_floor_exceeds_standard()
    {
        // 100 zero bytes; standard = 21000 + 100*4 = 21400; floor = 21000 + 100*10 = 22000
        var transaction = new Transaction { Data = new byte[100], To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + 100 * GasCostOf.TxDataZero,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 100));
    }

    [Test]
    public void floor_not_applied_before_eip7623()
    {
        // London predates EIP-7623 → FloorGas = 0, MinimalGas = Standard
        var transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, London.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxDataZero,
            FloorGas: 0));
    }

    [Test]
    public void access_list_with_nonzero_address_bytes_eip7981()
    {
        // Address: AB 00×18 CD → 2 non-zero (2*4=8 tokens) + 18 zero (18 tokens) = 26 tokens
        // standard = 21000 + 2400 + 26*10 = 23660; floor = 21000 + 26*10 = 21260
        byte[] addrBytes = new byte[20];
        addrBytes[0] = 0xAB;
        addrBytes[19] = 0xCD;
        AccessList accessList = new AccessList.Builder().AddAddress(new Address(addrBytes)).Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 26,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 26));
    }

    [Test]
    public void access_list_with_nonzero_storage_key_eip7981()
    {
        // Address.Zero = 20 tokens; UInt256.One = 31 zero bytes + 1 non-zero byte = 31 + 4 = 35 tokens; total = 55
        // standard = 21000 + 2400 + 1900 + 55*10 = 25850; floor = 21000 + 55*10 = 21550
        AccessList accessList = new AccessList.Builder()
            .AddAddress(Address.Zero)
            .AddStorage(UInt256.One)
            .Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.AccessAccountListEntry + GasCostOf.AccessStorageListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 55,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 55));
    }

    [Test]
    public void calldata_and_access_list_both_contribute_to_floor_eip7981()
    {
        // Data: [0] = 1 token in calldata; Address.Zero = 20 tokens; total = 21 tokens
        // standard = 21000 + 4 + 2400 + 20*10 = 23604; floor = 21000 + 21*10 = 21210
        AccessList accessList = new AccessList.Builder().AddAddress(Address.Zero).Build();
        Transaction transaction = new() { To = Address.Zero, Data = new byte[] { 0 }, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + GasCostOf.TxDataZero + GasCostOf.AccessAccountListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 20,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 21));
    }

    [Test]
    public void multiple_addresses_in_access_list_tokens_accumulate_eip7981()
    {
        // Address.Zero: 20 tokens; 0x00…01: 19 zero + 1 non-zero = 23 tokens; total = 43 tokens
        // standard = 21000 + 2*2400 + 43*10 = 26230; floor = 21000 + 43*10 = 21430
        byte[] addr1Bytes = new byte[20];
        addr1Bytes[19] = 0x01;
        AccessList accessList = new AccessList.Builder()
            .AddAddress(Address.Zero)
            .AddAddress(new Address(addr1Bytes))
            .Build();
        Transaction transaction = new() { To = Address.Zero, AccessList = accessList };
        EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Amsterdam.Instance);
        cost.Should().Be(new EthereumIntrinsicGas(
            Standard: GasCostOf.Transaction + 2 * GasCostOf.AccessAccountListEntry + GasCostOf.TotalCostFloorPerTokenEip7623 * 43,
            FloorGas: GasCostOf.Transaction + GasCostOf.TotalCostFloorPerTokenEip7623 * 43));
    }
}
