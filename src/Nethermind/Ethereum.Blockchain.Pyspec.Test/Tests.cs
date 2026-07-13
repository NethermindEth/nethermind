// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;

namespace Ethereum.Blockchain.Pyspec.Test;

// Blockchain tests - directory derived from class name by convention (strip "BlockchainTests", lowercase)
public class FrontierBlockchainTests : PyspecBlockchainTestFixture<FrontierBlockchainTests>;
public class HomesteadBlockchainTests : PyspecBlockchainTestFixture<HomesteadBlockchainTests>;
public class TangerineWhistleBlockchainTests : PyspecBlockchainTestFixture<TangerineWhistleBlockchainTests>;
public class SpuriousDragonBlockchainTests : PyspecBlockchainTestFixture<SpuriousDragonBlockchainTests>;
public class ByzantiumBlockchainTests : PyspecBlockchainTestFixture<ByzantiumBlockchainTests>;
public class IstanbulBlockchainTests : PyspecBlockchainTestFixture<IstanbulBlockchainTests>;
public class BerlinBlockchainTests : PyspecBlockchainTestFixture<BerlinBlockchainTests>;
public class ConstantinopleFixBlockchainTests : PyspecBlockchainTestFixture<ConstantinopleFixBlockchainTests>;
public class ParisBlockchainTests : PyspecBlockchainTestFixture<ParisBlockchainTests>;
public class LondonBlockchainTests : PyspecBlockchainTestFixture<LondonBlockchainTests>;
public class ShanghaiBlockchainTests : PyspecBlockchainTestFixture<ShanghaiBlockchainTests>;
public class CancunBlockchainTests : PyspecBlockchainTestFixture<CancunBlockchainTests>;
public class PragueBlockchainTests : PyspecBlockchainTestFixture<PragueBlockchainTests>;
public class OsakaBlockchainTests : PyspecBlockchainTestFixture<OsakaBlockchainTests>;
public class AmsterdamBlockchainTests : PyspecBlockchainTestFixture<AmsterdamBlockchainTests>;
public class ParisToShanghaiAtTime15kBlockchainTests : PyspecBlockchainTestFixture<ParisToShanghaiAtTime15kBlockchainTests>;
public class ShanghaiToCancunAtTime15kBlockchainTests : PyspecBlockchainTestFixture<ShanghaiToCancunAtTime15kBlockchainTests>;
public class CancunToPragueAtTime15kBlockchainTests : PyspecBlockchainTestFixture<CancunToPragueAtTime15kBlockchainTests>;
public class PragueToOsakaAtTime15kBlockchainTests : PyspecBlockchainTestFixture<PragueToOsakaAtTime15kBlockchainTests>;
public class OsakaToBpo1AtTime15kBlockchainTests : PyspecBlockchainTestFixture<OsakaToBpo1AtTime15kBlockchainTests>;
public class Bpo1ToBpo2AtTime15kBlockchainTests : PyspecBlockchainTestFixture<Bpo1ToBpo2AtTime15kBlockchainTests>;
public class Bpo2ToAmsterdamAtTime15kBlockchainTests : PyspecBlockchainTestFixture<Bpo2ToAmsterdamAtTime15kBlockchainTests>;
public class Bpo2ToBpo3AtTime15kBlockchainTests : PyspecBlockchainTestFixture<Bpo2ToBpo3AtTime15kBlockchainTests>;
public class Bpo3ToBpo4AtTime15kBlockchainTests : PyspecBlockchainTestFixture<Bpo3ToBpo4AtTime15kBlockchainTests>;

// Engine blockchain tests - post-merge forks with Engine API-specific coverage.
public class ParisEngineBlockchainTests : PyspecEngineBlockchainTestFixture<ParisEngineBlockchainTests>;
public class ShanghaiEngineBlockchainTests : PyspecEngineBlockchainTestFixture<ShanghaiEngineBlockchainTests>;
public class CancunEngineBlockchainTests : PyspecEngineBlockchainTestFixture<CancunEngineBlockchainTests>;
public class PragueEngineBlockchainTests : PyspecEngineBlockchainTestFixture<PragueEngineBlockchainTests>;
public class OsakaEngineBlockchainTests : PyspecEngineBlockchainTestFixture<OsakaEngineBlockchainTests>;
public class AmsterdamEngineBlockchainTests : PyspecEngineBlockchainTestFixture<AmsterdamEngineBlockchainTests>;
public class ParisToShanghaiAtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<ParisToShanghaiAtTime15kEngineBlockchainTests>;
public class ShanghaiToCancunAtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<ShanghaiToCancunAtTime15kEngineBlockchainTests>;
public class CancunToPragueAtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<CancunToPragueAtTime15kEngineBlockchainTests>;
public class PragueToOsakaAtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<PragueToOsakaAtTime15kEngineBlockchainTests>;
public class OsakaToBpo1AtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<OsakaToBpo1AtTime15kEngineBlockchainTests>;
public class Bpo1ToBpo2AtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<Bpo1ToBpo2AtTime15kEngineBlockchainTests>;
public class Bpo2ToAmsterdamAtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<Bpo2ToAmsterdamAtTime15kEngineBlockchainTests>;
public class Bpo2ToBpo3AtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<Bpo2ToBpo3AtTime15kEngineBlockchainTests>;
public class Bpo3ToBpo4AtTime15kEngineBlockchainTests : PyspecEngineBlockchainTestFixture<Bpo3ToBpo4AtTime15kEngineBlockchainTests>;

// EIP-7805 (FOCIL) — Bogota fork. Loads `for_bogota` from the tests-focil release.
public class BogotaEngineBlockchainTests : PyspecBogotaEngineBlockchainTestFixture;

// Sync blockchain tests - exercise sync-mode payload validation alongside the standard engine flow.
public class AmsterdamSyncBlockchainTests : PyspecSyncBlockchainTestFixture<AmsterdamSyncBlockchainTests>;
public class OsakaSyncBlockchainTests : PyspecSyncBlockchainTestFixture<OsakaSyncBlockchainTests>;

// Amsterdam parallel-execution / batch-read prewarm variants - Linux x64 only.
// Each combo gets its own fixture so the FlatDB workflow can chunk them on dedicated matrix entries.
// Default chunks exclude these classes via FullyQualifiedName filter to avoid double-running.
public class AmsterdamParallelBlockchainTests() : PyspecAmsterdamBlockchainTestFixture(parallel: true, batchRead: false);
public class AmsterdamBatchReadBlockchainTests() : PyspecAmsterdamBlockchainTestFixture(parallel: false, batchRead: true);
public class AmsterdamParallelFullBlockchainTests() : PyspecAmsterdamBlockchainTestFixture(parallel: true, batchRead: true);
public class AmsterdamParallelEngineBlockchainTests() : PyspecAmsterdamEngineBlockchainTestFixture(parallel: true, batchRead: false);
public class AmsterdamBatchReadEngineBlockchainTests() : PyspecAmsterdamEngineBlockchainTestFixture(parallel: false, batchRead: true);
public class AmsterdamParallelFullEngineBlockchainTests() : PyspecAmsterdamEngineBlockchainTestFixture(parallel: true, batchRead: true);

// State tests - directory derived from class name by convention (strip "StateTests", lowercase)
public class FrontierStateTests : PyspecStateTestFixture<FrontierStateTests>;
public class HomesteadStateTests : PyspecStateTestFixture<HomesteadStateTests>;
public class TangerineWhistleStateTests : PyspecStateTestFixture<TangerineWhistleStateTests>;
public class SpuriousDragonStateTests : PyspecStateTestFixture<SpuriousDragonStateTests>;
public class ByzantiumStateTests : PyspecStateTestFixture<ByzantiumStateTests>;
public class IstanbulStateTests : PyspecStateTestFixture<IstanbulStateTests>;
public class BerlinStateTests : PyspecStateTestFixture<BerlinStateTests>;
public class ConstantinopleFixStateTests : PyspecStateTestFixture<ConstantinopleFixStateTests>;
public class LondonStateTests : PyspecStateTestFixture<LondonStateTests>;
public class ParisStateTests : PyspecStateTestFixture<ParisStateTests>;
public class ShanghaiStateTests : PyspecStateTestFixture<ShanghaiStateTests>;
public class CancunStateTests : PyspecStateTestFixture<CancunStateTests>;
public class PragueStateTests : PyspecStateTestFixture<PragueStateTests>;
public class OsakaStateTests : PyspecStateTestFixture<OsakaStateTests>;
public class AmsterdamStateTests : PyspecStateTestFixture<AmsterdamStateTests>;
public class ShanghaiToCancunAtTime15kStateTests : PyspecStateTestFixture<ShanghaiToCancunAtTime15kStateTests>;
public class CancunToPragueAtTime15kStateTests : PyspecStateTestFixture<CancunToPragueAtTime15kStateTests>;

// Transaction tests - validate raw tx decoding + validation against per-fork expected exceptions.
public class AmsterdamTransactionTests : PyspecTransactionTestFixture<AmsterdamTransactionTests>;
public class OsakaTransactionTests : PyspecTransactionTestFixture<OsakaTransactionTests>;
public class PragueTransactionTests : PyspecTransactionTestFixture<PragueTransactionTests>;
