// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvm;

// Blockchain tests

[EipWildcard("eip7708_eth_transfer_logs")]
public class Eip7708BlockChainTests : ZkEvmBlockChainTestFixture<Eip7708BlockChainTests>;

[EipWildcard("eip7778_block_gas_accounting_without_refunds")]
public class Eip7778BlockChainTests : ZkEvmBlockChainTestFixture<Eip7778BlockChainTests>;

[EipWildcard("eip7843_slotnum")]
public class Eip7843BlockChainTests : ZkEvmBlockChainTestFixture<Eip7843BlockChainTests>;

[EipWildcard("eip7928_block_level_access_lists")]
public class Eip7928BlockChainTests : ZkEvmBlockChainTestFixture<Eip7928BlockChainTests>;

[EipWildcard("eip7954_increase_max_contract_size")]
public class Eip7954BlockChainTests : ZkEvmBlockChainTestFixture<Eip7954BlockChainTests>;

[EipWildcard("eip8024_dupn_swapn_exchange")]
public class Eip8024BlockChainTests : ZkEvmBlockChainTestFixture<Eip8024BlockChainTests>;

[EipWildcard("eip8025_optional_proofs")]
public class Eip8025BlockChainTests : ZkEvmBlockChainTestFixture<Eip8025BlockChainTests>;

[EipWildcard("eip8037_state_creation_gas_cost_increase")]
public class Eip8037BlockChainTests : ZkEvmBlockChainTestFixture<Eip8037BlockChainTests>;
