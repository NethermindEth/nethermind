// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

// Each class below pairs an EIP with its fixture subdirectory inside for_amsterdam/.
// The path must match the directory that exists in the BAL archive — no wildcards.

[AmsterdamFixturePath("eip7708_eth_transfer_logs")]
public class Eip7708BlockChainTests : AmsterdamBlockChainTestFixture<Eip7708BlockChainTests>;

[AmsterdamFixturePath("eip7708_eth_transfer_logs")]
public class Eip7708EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7708EngineBlockChainTests>;

[AmsterdamFixturePath("eip7778_block_gas_accounting_without_refunds")]
public class Eip7778BlockChainTests : AmsterdamBlockChainTestFixture<Eip7778BlockChainTests>;

[AmsterdamFixturePath("eip7778_block_gas_accounting_without_refunds")]
public class Eip7778EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7778EngineBlockChainTests>;

[AmsterdamFixturePath("eip7843_slotnum")]
public class Eip7843BlockChainTests : AmsterdamBlockChainTestFixture<Eip7843BlockChainTests>;

[AmsterdamFixturePath("eip7843_slotnum")]
public class Eip7843EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7843EngineBlockChainTests>;

[AmsterdamFixturePath("eip7928_block_level_access_lists")]
[TestFixture(false)]
[TestFixture(true)]
public class Eip7928BlockChainTests(bool parallel) : AmsterdamBlockChainTestFixture<Eip7928BlockChainTests>
{
    protected override bool? ParallelExecutionOverride => parallel;
}

[AmsterdamFixturePath("eip7928_block_level_access_lists")]
[TestFixture(false)]
[TestFixture(true)]
public class Eip7928EngineBlockChainTests(bool parallel) : AmsterdamEngineBlockChainTestFixture<Eip7928EngineBlockChainTests>
{
    protected override bool? ParallelExecutionOverride => parallel;
}

[AmsterdamFixturePath("eip7954_increase_max_contract_size")]
public class Eip7954BlockChainTests : AmsterdamBlockChainTestFixture<Eip7954BlockChainTests>;

[AmsterdamFixturePath("eip7954_increase_max_contract_size")]
public class Eip7954EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7954EngineBlockChainTests>;

[AmsterdamFixturePath("eip8024_dupn_swapn_exchange")]
public class Eip8024BlockChainTests : AmsterdamBlockChainTestFixture<Eip8024BlockChainTests>;

[AmsterdamFixturePath("eip8024_dupn_swapn_exchange")]
public class Eip8024EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip8024EngineBlockChainTests>;

[AmsterdamFixturePath("eip8037_state_creation_gas_cost_increase")]
public class Eip8037BlockChainTests : AmsterdamBlockChainTestFixture<Eip8037BlockChainTests>;

[AmsterdamFixturePath("eip8037_state_creation_gas_cost_increase")]
public class Eip8037EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip8037EngineBlockChainTests>;

// State tests

[AmsterdamFixturePath("eip7708_eth_transfer_logs")]
public class Eip7708StateTests : AmsterdamStateTestFixture<Eip7708StateTests>;

[AmsterdamFixturePath("eip7843_slotnum")]
public class Eip7843StateTests : AmsterdamStateTestFixture<Eip7843StateTests>;

[AmsterdamFixturePath("eip7954_increase_max_contract_size")]
public class Eip7954StateTests : AmsterdamStateTestFixture<Eip7954StateTests>;

[AmsterdamFixturePath("eip8024_dupn_swapn_exchange")]
public class Eip8024StateTests : AmsterdamStateTestFixture<Eip8024StateTests>;

[AmsterdamFixturePath("eip8037_state_creation_gas_cost_increase")]
public class Eip8037StateTests : AmsterdamStateTestFixture<Eip8037StateTests>;
