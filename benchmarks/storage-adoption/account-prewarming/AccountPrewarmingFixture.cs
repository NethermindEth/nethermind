using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

sealed class AccountPrewarmingFixture : IDisposable
{
    private readonly string _directory;
    private readonly string _shape;
    private readonly Address[] _addresses;
    private readonly Dictionary<Address, Account?> _expected = [];
    private readonly Transaction[] _transactions;
    private readonly KnownParent _parents = new();
    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private PreBlockCaches _caches = null!;
    private IDisposable _warmer = null!;
    private BlockCachePreWarmer _prewarmer = null!;
    public int CacheMisses { get; private set; }
    private Action<ParallelOptions, Block> _pass = null!;
    private Block _block = null!;
    private readonly ParallelOptions _options = new() { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };

    public AccountPrewarmingFixture(int count, string shape)
    {
        _shape = shape;
        _directory = Path.Combine(Environment.GetEnvironmentVariable("ACCOUNT_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        int accountCount = shape == "repeated" ? 2 : 2 * count;
        _addresses = new Address[accountCount];
        for (int i = 0; i < accountCount; i++)
        {
            byte[] bytes = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), i + 1);
            Address address = _addresses[i] = new(bytes);
            _expected[address] = i % 8 == 7 ? null : new Account((ulong)(i + 1), (UInt256.One << 200) + (ulong)(i + 1));
        }
        _transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            _transactions[i] = Build.A.Transaction.WithTo(_addresses[(2 * i + 1) % accountCount]).TestObject;
            _transactions[i].SenderAddress = _addresses[(2 * i) % accountCount];
        }
        Open(seed: true);
    }

    private void Open(bool seed)
    {
        bool detailed = Environment.GetEnvironmentVariable("DETAILED_METRICS") == "1";
        Nethermind.Db.Metrics.DetailedMetricsEnabled = detailed;
        ConfigProvider config = new(
            new FlatDbConfig { Enabled = true, VerifyWithTrie = false, EnableCarryForwardCache = _shape != "native-warm" },
            new InitConfig { BaseDbPath = _directory },
            new MetricsConfig { Enabled = true, EnableDetailedMetric = detailed },
            new DbConfig { SharedBlockCacheSize = 32UL * 1024 * 1024 });
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config))
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat, static settings => settings.PersistRepairMarkerUntilAcknowledged = true)
            .AddSingleton<IRocksDbConfigFactory>(ctx => new RocksDbConfigFactory(ctx.Resolve<IDbConfig>(),
                ctx.Resolve<IPruningConfig>(), ctx.Resolve<IHardwareInfo>(), ctx.Resolve<ILogManager>(), validateConfig: false))
            .AddSingleton<IStateHeaderProvider>(_parents)
            .AddSingleton(new PreBlockCachesConfig { StateCacheSetsBits = 16 })
            .Build();
        if (_container.Resolve<IColumnsDb<FlatDbColumns>>() is not ColumnsDb<FlatDbColumns> database)
            throw new InvalidOperationException("Account prewarming must use native RocksDB columns");
        IWorldStateManager manager = _container.Resolve<IWorldStateManager>();
        IMainProcessingModule[] modules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(builder =>
        {
            builder.RegisterInstance(manager.GlobalWorldState).As<IWorldStateScopeProvider>().ExternallyOwned();
            builder.RegisterInstance(manager).As<IWorldStateManager>().ExternallyOwned();
            builder.AddModule(modules);
        });
        if (seed)
        {
            IWorldState state = _processingScope.Resolve<IWorldState>();
            using (state.BeginScope(IWorldState.PreGenesis))
            {
                foreach ((Address address, Account? account) in _expected)
                    if (account is not null) state.CreateAccount(address, account.Balance, account.Nonce);
                state.Commit(Osaka.Instance, isGenesis: true);
                state.CommitTree(0);
                _parents.Parent = Build.A.BlockHeader.WithNumber(0).WithStateRoot(state.StateRoot).TestObject;
            }
            if (_shape != "snapshot")
            {
                _container.Resolve<IFlatDbManager>().FlushCache(CancellationToken.None);
                database.Flush();
            }
        }
        _block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithParent(_parents.Parent!).TestObject)
            .WithTransactions(_transactions[..(_transactions.Length / 2)]).TestObject;
        _block.InclusionListTransactions = _transactions[(_transactions.Length / 2)..];
        _caches = _processingScope.Resolve<PreBlockCaches>();
        _prewarmer = (BlockCachePreWarmer)_processingScope.Resolve<IBlockCachePreWarmer>();
        (_warmer, _pass) = CreatePass(_prewarmer);
        Nethermind.Db.Metrics.DetailedMetricsEnabled = detailed;
    }

    private (IDisposable Warmer, Action<ParallelOptions, Block> Pass) CreatePass(BlockCachePreWarmer prewarmer)
    {
        Type warmerType = typeof(BlockCachePreWarmer).GetNestedType("AddressWarmer", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = warmerType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        object instance = constructor.Invoke([_options, _block, Osaka.Instance, false, false, prewarmer, null]);
        warmerType.GetProperty("Beneficiary")!.SetValue(instance, null);
        Action<ParallelOptions, Block> pass = warmerType.GetMethod("WarmupAddresses", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Action<ParallelOptions, Block>>(instance);
        return ((IDisposable)instance, pass);
    }

    public void Prepare()
    {
        _caches.ClearCaches();
        _caches.PrepareFor(_parents.Parent!.StateRoot);
        for (int i = 0; i < _addresses.Length; i++)
        {
            bool cached = _shape == "shared" || _shape == "mixed" && (i & 1) == 0
                || _shape == "tail1" && i < _addresses.Length - 1
                || _shape == "tail7" && i < _addresses.Length - 7;
            if (!cached) continue;
            AddressAsKey key = _addresses[i];
            _caches.StateCache.Set(in key, _expected[_addresses[i]]);
        }
    }

    public void Read() => _pass(_options, _block);

    public void Verify()
    {
        Prepare();
        AccountReadVerification verification = new(_expected);
        using (BlockCachePreWarmer prewarmer = verification.CreateWarmer(_prewarmer, _caches, _container.Resolve<ILogManager>()))
        {
            (IDisposable warmer, Action<ParallelOptions, Block> pass) = CreatePass(prewarmer);
            using (warmer) pass(_options, _block);
        }
        verification.AssertComplete();
        VerifyResults();
    }

    public void VerifyResults()
    {
        CacheMisses = 0;
        foreach ((Address address, Account? expected) in _expected)
        {
            AddressAsKey key = address;
            if (!_caches.StateCache.TryGetValue(in key, out Account? actual)) CacheMisses++;
            else if (actual != expected)
                throw new InvalidOperationException("Retained account value mismatch: " + address);
        }
        if (CacheMisses != 0) Console.WriteLine($"Shared cache retained {_expected.Count - CacheMisses}/{_expected.Count} resolved accounts");
        using IPersistence.IPersistenceReader reader = _container.Resolve<IPersistence>().CreateReader();
        if (_shape != "snapshot" && reader.CurrentState != new StateId(_parents.Parent))
            throw new InvalidOperationException("Fixture must read the persisted parent snapshot");
    }

    public void ReopenCold()
    {
        Close();
        ProcessStartInfo start = new("/usr/bin/sudo") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-n", "/bin/sh", "-c", "sync; echo 3 > /proc/sys/vm/drop_caches" }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cold-cache reset failed: " + process.StandardError.ReadToEnd());
        Open(seed: false);
    }

    private void Close()
    {
        _warmer?.Dispose();
        _processingScope?.Dispose();
        _container?.Dispose();
    }

    public void Dispose()
    {
        Close();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class KnownParent : IStateHeaderProvider
    {
        internal BlockHeader? Parent { get; set; }
        public BlockHeader? FindParentHeader(BlockHeader target) => target.ParentHash == Parent?.Hash ? Parent : null;
        public ulong FinalizedBlockNumber => 0;
        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => blockNumber == 0 ? Parent : null;
    }
}
