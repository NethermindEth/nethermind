using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.VerkleMigration.Cli;

internal class Migrator
{
    private const string ConfigPath = "./config.cfg";
    private static readonly ILogManager LogManager = SimpleConsoleLogManager.Instance;
    private static readonly ILogger Logger = new(SimpleConsoleLogger.Instance);

    private static readonly ILogManager MigratorLogManager = SimpleConsoleLogManager.Instance;
    private static readonly ProcessExitSource ProcessExitSource = new();

    public static async Task Main(string[] args)
    {
        // 1. Get config path
        var configPath = ConfigPath;
        if (args.Length > 0) configPath = args[1];

        Logger.Info($"Running migration tool with config path: {configPath}");

        // 2. Setup migrator
        (INethermindApi api, VerkleTreeMigrator migrator) = await SetupMigrator(configPath);

        // 3. Run the migrator
        api.StateReader!.RunTreeVisitor(migrator, api.WorldState!.StateRoot);
        migrator.FinalizeMigration(0);

        Logger.Info($"Merkle tree root hash: {api.WorldState!.StateRoot}");
        Logger.Info($"Verkle tree root hash: {migrator.VerkleStateTree.StateRoot}");

        TrieStats merkleTreeStats =
            api.StateReader.CollectStats(api.WorldState.StateRoot, api.DbProvider!.CodeDb, LogManager);
        Logger.Info($"Merkle tree stats: {merkleTreeStats}");

        // TrieStatsCollector collector = new(api.DbProvider!.CodeDb, _logManager);
        // migrator._verkleStateTree.Accept(collector, migrator._verkleStateTree.StateRoot);
        // TrieStats verkleTreeStats = collector.Stats;

        // Logger.Info($"Verkle tree stats: {verkleTreeStats}");
    }

    private static async Task<(INethermindApi, VerkleTreeMigrator)> SetupMigrator(string configPath)
    {
        Logger.Info("Starting api...");

        ConfigProvider configProvider = new();
        configProvider.AddSource(new JsonConfigSource(configPath));
        configProvider.Initialize();
        ApiBuilder apiBuilder = new(configProvider, LogManager);
        INethermindApi nethermindApi = apiBuilder.Create();
        nethermindApi.ProcessExit = ProcessExitSource;

        IInitConfig config = nethermindApi.Config<IInitConfig>();
        config.DiagnosticMode = DiagnosticMode.VerifyTrie;

        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.PersistantDb, config.BaseDbPath);
        nethermindApi.DbProvider = dbProvider;
        nethermindApi.EthereumEcdsa = new EthereumEcdsa(nethermindApi.ChainSpec.ChainId, nethermindApi.LogManager);

        // setup database data
        IStep initializer = new MigrateConfigs(nethermindApi);
        await initializer.Execute(ProcessExitSource.Token);
        initializer = new ApplyMemoryHint(nethermindApi);
        await initializer.Execute(ProcessExitSource.Token);
        initializer = new InitDatabase(nethermindApi);
        await initializer.Execute(ProcessExitSource.Token);
        initializer = new InitRlp(nethermindApi);
        await initializer.Execute(ProcessExitSource.Token);
        initializer = new InitializeBlockTree(nethermindApi);
        await initializer.Execute(ProcessExitSource.Token);
        initializer = new InitializeStateDb(nethermindApi);
        await initializer.Execute(ProcessExitSource.Token);

        // initialize verkle tree
        var verkleStore = new VerkleTreeStore<PersistEveryBlock>(nethermindApi.DbProvider, LogManager);
        var verkleStateTree = new VerkleStateTree(verkleStore, LogManager);

        // initialize migrator
        var migrator = new VerkleTreeMigrator(verkleStateTree, nethermindApi.StateReader!, MigratorLogManager, null,
            ProgressChangedHandler);

        return (nethermindApi, migrator);
    }


    private static void ProgressChangedHandler(object? sender, VerkleTreeMigrator.ProgressEventArgs e)
    {
        Logger.Info(
            $"Progress: {e.Progress:F2}% Time since last update: {e.TimeSinceLastUpdate.Microseconds:F2}μs Current address: {e.CurrentAddress?.ToString() ?? "null"} Is storage: {e.IsStorage}");
    }
}
