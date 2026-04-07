// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test;

// Blockchain tests - directory derived from class name by convention (strip "BlockchainTests", lowercase)

public class FrontierBlockchainTests : PyspecBlockchainTestFixture<FrontierBlockchainTests>;

public class HomesteadBlockchainTests : PyspecBlockchainTestFixture<HomesteadBlockchainTests>;

public class ByzantiumBlockchainTests : PyspecBlockchainTestFixture<ByzantiumBlockchainTests>;

public class IstanbulBlockchainTests : PyspecBlockchainTestFixture<IstanbulBlockchainTests>;

public class BerlinBlockchainTests : PyspecBlockchainTestFixture<BerlinBlockchainTests>;

public class ParisBlockchainTests : PyspecBlockchainTestFixture<ParisBlockchainTests>;

public class ShanghaiBlockchainTests : PyspecBlockchainTestFixture<ShanghaiBlockchainTests>;

public class CancunBlockchainTests : PyspecBlockchainTestFixture<CancunBlockchainTests>;

public class PragueBlockchainTests : PyspecBlockchainTestFixture<PragueBlockchainTests>;

public class OsakaBlockchainTests : PyspecBlockchainTestFixture<OsakaBlockchainTests>;

// Engine blockchain tests — only forks with meaningful Engine API differences
// (e.g. blobs, execution requests, BAL). Regular BlockchainTests cover earlier forks.
// Directory derived from class name by convention (strip "EngineBlockchainTests", lowercase)

public class CancunEngineBlockchainTests : PyspecEngineBlockchainTestFixture<CancunEngineBlockchainTests>;

public class PragueEngineBlockchainTests : PyspecEngineBlockchainTestFixture<PragueEngineBlockchainTests>;

public class OsakaEngineBlockchainTests : PyspecEngineBlockchainTestFixture<OsakaEngineBlockchainTests>;

// State tests - directory derived from class name by convention (strip "StateTests", lowercase)

public class FrontierStateTests : PyspecStateTestFixture<FrontierStateTests>;

public class HomesteadStateTests : PyspecStateTestFixture<HomesteadStateTests>;

public class ByzantiumStateTests : PyspecStateTestFixture<ByzantiumStateTests>;

public class IstanbulStateTests : PyspecStateTestFixture<IstanbulStateTests>;

public class BerlinStateTests : PyspecStateTestFixture<BerlinStateTests>;

public class ShanghaiStateTests : PyspecStateTestFixture<ShanghaiStateTests>;

public class CancunStateTests : PyspecStateTestFixture<CancunStateTests>;

public class PragueStateTests : PyspecStateTestFixture<PragueStateTests>;

public class OsakaStateTests : PyspecStateTestFixture<OsakaStateTests>;
