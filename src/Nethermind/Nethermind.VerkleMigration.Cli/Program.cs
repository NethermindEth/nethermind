using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.VerkleTransition.Cli;

namespace Nethermind.VerkleMigration.Cli
{
    class Program
    {
        private static ILogManager _logManager = SimpleConsoleLogManager.Instance;
        private static readonly string dbPath = "../artifacts/bin/Nethermind.Runner/debug/nethermind_db/mainnet_archive";

        public static void Main(string[] args)
        {
            Console.WriteLine($"Running migration tool on db path: {dbPath}");

            // 1. Get db path
            // dbPath = RequestDbPath();

            // 2. Restore state from db
            (VerkleTreeMigrator migrator, StateTree merkleTree) = RestoreState(dbPath);

            // 3. Run the Verkle Tree migrator
            merkleTree.Accept(migrator, merkleTree.RootHash);
            migrator.FinalizeMigration(10);

            Console.WriteLine($"Merkle tree root hash: {merkleTree.RootHash}");
            Console.WriteLine($"Verkle tree root hash: {migrator._verkleStateTree.StateRoot}");

        }

        static string RequestDbPath()
        {
            Console.Write("Enter the path to the database: ");
            string? dbPath = Console.ReadLine();

            while (!Directory.Exists(dbPath))
            {
                Console.WriteLine("The specified path does not exist. Please enter a valid path.");
                Console.Write("Enter the path to the database: ");
                dbPath = Console.ReadLine();
            }

            return dbPath;
        }

        static (VerkleTreeMigrator migrator, StateTree merkleTree) RestoreState(string dbPath)
        {
            Console.WriteLine("Restoring state...");

            // var settings = new DbSettings("Preimages db", DbNames.Preimages);
            // var config = new DbConfig();

            IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.PersistantDb, dbPath);
            IDb preimagesDb = dbProvider.GetDb<IDb>(DbNames.Preimages);
            // IDb preimagesDb = new MemDb();

            // PopulatePreimagesDb(preimagesDb);

            // initialize merkle tree
            var trieStore = new TrieStore(dbProvider.CodeDb, _logManager);
            var merkleStateTree = new StateTree(trieStore, _logManager);
            IStateReader stateReader = new StateReader(trieStore, dbProvider.CodeDb, _logManager);

            // initialize verkle tree
            var verkleStore = new VerkleTreeStore<VerkleSyncCache>(dbProvider, _logManager);
            var verkleStateTree = new VerkleStateTree(verkleStore, _logManager);

            var migrator = new VerkleTreeMigrator(verkleStateTree, stateReader, preimagesDb);

            return (migrator, merkleStateTree);
        }
    }
}
