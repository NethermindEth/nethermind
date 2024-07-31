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
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.VerkleTransition.Cli;

namespace Nethermind.VerkleMigration.Cli
{
    class Program
    {
        private static ILogManager _logManager = SimpleConsoleLogManager.Instance;
        private static readonly string _configPath = "./config.cfg";
        private static readonly ProcessExitSource _processExitSource = new();


        public static async Task Main(string[] args)
        {
            // 1. Get config path
            string configPath = _configPath;
            if (args.Length > 0)
            {
                configPath = args[1];
            }

            Console.WriteLine($"Running migration tool with config path: {configPath}");

            // 2. Setup migrator
            (INethermindApi api, VerkleTreeMigrator migrator) = await SetupMigrator(configPath);

            // 3. Run the migrator
            api.StateReader!.RunTreeVisitor(migrator, api.WorldState!.StateRoot);
            migrator.FinalizeMigration(0);

            Console.WriteLine($"Merkle tree root hash: {api.WorldState!.StateRoot}");
            Console.WriteLine($"Verkle tree root hash: {migrator._verkleStateTree.StateRoot}");
        }

        static async Task<(INethermindApi, VerkleTreeMigrator)> SetupMigrator(string configPath)
        {
            Console.WriteLine("Starting api...");

            ConfigProvider configProvider = new();
            configProvider.AddSource(new JsonConfigSource(configPath));
            configProvider.Initialize();
            ApiBuilder apiBuilder = new(configProvider, _logManager);
            INethermindApi nethermindApi = apiBuilder.Create([]);
            nethermindApi.ProcessExit = _processExitSource;

            IInitConfig config = nethermindApi.Config<IInitConfig>();
            config.DiagnosticMode = DiagnosticMode.VerifyTrie;

            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.PersistantDb, config.BaseDbPath);

            nethermindApi.DbProvider = dbProvider;
            nethermindApi.EthereumEcdsa = new EthereumEcdsa(nethermindApi.ChainSpec.ChainId, nethermindApi.LogManager);

            // setup database data
            IStep initializer = new MigrateConfigs(nethermindApi);
            await initializer.Execute(_processExitSource.Token);
            initializer = new ApplyMemoryHint(nethermindApi);
            await initializer.Execute(_processExitSource.Token);
            initializer = new InitDatabase(nethermindApi);
            await initializer.Execute(_processExitSource.Token);
            initializer = new InitRlp(nethermindApi);
            await initializer.Execute(_processExitSource.Token);
            initializer = new InitializeBlockTree(nethermindApi);
            await initializer.Execute(_processExitSource.Token);
            initializer = new InitializeStateDb(nethermindApi);
            await initializer.Execute(_processExitSource.Token);

            // initialize verkle tree
            var verkleStore = new VerkleTreeStore<VerkleSyncCache>(nethermindApi.DbProvider, _logManager);
            var verkleStateTree = new VerkleStateTree(verkleStore, _logManager);
            // initialize migrator
            IDb preimagesDb = dbProvider.GetDb<IDb>(DbNames.Preimages);
            var migrator = new VerkleTreeMigrator(verkleStateTree, nethermindApi.StateReader!, preimagesDb);

            return (nethermindApi, migrator);
        }
    }
}
