// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

// Blockchain tests

[EipWildcard("eip7708_eth_transfer_logs")]
public class Eip7708BlockChainTests : AmsterdamBlockChainTestFixture<Eip7708BlockChainTests>;

[EipWildcard("eip7708_eth_transfer_logs")]
public class Eip7708EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7708EngineBlockChainTests>;

[EipWildcard("eip7778_block_gas_accounting_without_refunds")]
public class Eip7778BlockChainTests : AmsterdamBlockChainTestFixture<Eip7778BlockChainTests>;

[EipWildcard("eip7778_block_gas_accounting_without_refunds")]
public class Eip7778EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7778EngineBlockChainTests>;

[EipWildcard("eip7843_slotnum")]
public class Eip7843BlockChainTests : AmsterdamBlockChainTestFixture<Eip7843BlockChainTests>;

[EipWildcard("eip7843_slotnum")]
public class Eip7843EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7843EngineBlockChainTests>;

[EipWildcard("eip7928_block_level_access_lists")]
public class Eip7928BlockChainTests : AmsterdamBlockChainTestFixture<Eip7928BlockChainTests>;

[EipWildcard("eip7928_block_level_access_lists")]
public class Eip7928EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7928EngineBlockChainTests>;

[EipWildcard("eip7954_increase_max_contract_size")]
public class Eip7954BlockChainTests : AmsterdamBlockChainTestFixture<Eip7954BlockChainTests>;

[EipWildcard("eip7954_increase_max_contract_size")]
public class Eip7954EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip7954EngineBlockChainTests>;

[EipWildcard("eip8024_dupn_swapn_exchange")]
public class Eip8024BlockChainTests : AmsterdamBlockChainTestFixture<Eip8024BlockChainTests>;

[EipWildcard("eip8024_dupn_swapn_exchange")]
public class Eip8024EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip8024EngineBlockChainTests>;

[EipWildcard("eip8037_state_creation_gas_cost_increase")]
public class Eip8037BlockChainTests : AmsterdamBlockChainTestFixture<Eip8037BlockChainTests>;

[EipWildcard("eip8037_state_creation_gas_cost_increase")]
public class Eip8037EngineBlockChainTests : AmsterdamEngineBlockChainTestFixture<Eip8037EngineBlockChainTests>;

// State tests

[EipWildcard("eip7708_eth_transfer_logs")]
public class Eip7708StateTests : AmsterdamStateTestFixture<Eip7708StateTests>;

[EipWildcard("eip7843_slotnum")]
public class Eip7843StateTests : AmsterdamStateTestFixture<Eip7843StateTests>;

[EipWildcard("eip7954_increase_max_contract_size")]
public class Eip7954StateTests : AmsterdamStateTestFixture<Eip7954StateTests>;

[EipWildcard("eip8024_dupn_swapn_exchange")]
public class Eip8024StateTests : AmsterdamStateTestFixture<Eip8024StateTests>;

[EipWildcard("eip8037_state_creation_gas_cost_increase")]
public class Eip8037StateTests : AmsterdamStateTestFixture<Eip8037StateTests>;
