using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

sealed class AccountReadVerification(IReadOnlyDictionary<Address, Account?> expected)
{
    private static readonly FieldInfo WrappedState = typeof(WorldStateDecorator).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Provider = typeof(WorldState).GetField("_stateProvider", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Copy = Provider.FieldType.GetMethod("CopyAccountChanges", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly Dictionary<Address, Account?> _resolved = [];
    private readonly List<Exception> _errors = [];
    private readonly Lock _sync = new();

    public BlockCachePreWarmer CreateWarmer(BlockCachePreWarmer original, PreBlockCaches caches, ILogManager logManager)
    {
        ObjectPool<IPrewarmerEnv> pool = (ObjectPool<IPrewarmerEnv>)ReadField(original, "_envPool")!;
        ConstructorInfo constructor = typeof(BlockCachePreWarmer).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters()[0].ParameterType == typeof(IPooledObjectPolicy<IPrewarmerEnv>));
        return (BlockCachePreWarmer)constructor.Invoke([
            new CapturePolicy(pool, this), Environment.ProcessorCount * 2,
            ReadField(original, "_concurrencyLevel"), ReadField(original, "_parallelExecutionBatchRead"),
            ReadField(original, "_nodeStorageCache"), caches, logManager,
            ReadField(original, "_speculativeConcurrencyLevel"), ReadField(original, "_senderRecovery"),
            ReadField(original, "_handoff")]);
    }

    private static object? ReadField(BlockCachePreWarmer warmer, string name) =>
        typeof(BlockCachePreWarmer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(warmer);

    private static ArrayPoolList<KeyValuePair<AddressAsKey, Account?>> CopyResolved(IWorldState state)
    {
        while (state is WorldStateDecorator) state = (IWorldState)WrappedState.GetValue(state)!;
        if (state is not WorldState) throw new InvalidOperationException("Unknown prewarming world-state implementation");
        return (ArrayPoolList<KeyValuePair<AddressAsKey, Account?>>)Copy.Invoke(Provider.GetValue(state), null)!;
    }

    private static void AssertEmpty(IWorldState state)
    {
        using ArrayPoolList<KeyValuePair<AddressAsKey, Account?>> accounts = CopyResolved(state);
        if (accounts.Count != 0) throw new InvalidOperationException("Verification scope must start without account reads");
    }

    private void Record(IWorldState state)
    {
        try
        {
            using ArrayPoolList<KeyValuePair<AddressAsKey, Account?>> accounts = CopyResolved(state);
            lock (_sync)
            {
                foreach (KeyValuePair<AddressAsKey, Account?> account in accounts)
                {
                    Address address = account.Key.Value;
                    if (!expected.TryGetValue(address, out Account? wanted) || wanted != account.Value)
                        throw new InvalidOperationException("Resolved account mismatch: " + address);
                    _resolved[address] = account.Value;
                }
            }
        }
        catch (Exception error)
        {
            lock (_sync) _errors.Add(error);
        }
    }

    public void AssertComplete()
    {
        if (_errors.Count != 0) throw new AggregateException("Account read verification failed", _errors);
        foreach ((Address address, Account? account) in expected)
            if (!_resolved.TryGetValue(address, out Account? actual) || actual != account)
                throw new InvalidOperationException("Prewarming did not resolve the expected account: " + address);
    }

    private sealed class CapturePolicy(ObjectPool<IPrewarmerEnv> pool, AccountReadVerification verification) : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public IPrewarmerEnv Create() => new CapturingEnv(pool, pool.Get(), verification);
        public bool Return(IPrewarmerEnv item)
        {
            item.Dispose();
            return false;
        }
    }

    private sealed class CapturingEnv(ObjectPool<IPrewarmerEnv> pool, IPrewarmerEnv inner, AccountReadVerification verification) : IPrewarmerEnv
    {
        private IPrewarmerEnv? _inner = inner;
        private IPrewarmerEnv Inner => _inner ?? throw new ObjectDisposedException(nameof(CapturingEnv));
        public ReadOnlySpan<IHasAccessList> SystemAccessLists => Inner.SystemAccessLists;

        public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
        {
            if (!Inner.TryBuild(baseBlock, out scope)) return false;
            scope = Wrap(scope);
            return true;
        }

        public bool TryBuildAtTarget(BlockHeader target, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
        {
            if (!Inner.TryBuildAtTarget(target, out scope)) return false;
            scope = Wrap(scope);
            return true;
        }

        private CapturingScope Wrap(IReadOnlyTxProcessingScope scope)
        {
            try
            {
                AssertEmpty(scope.WorldState);
                return new CapturingScope(scope, verification);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            IPrewarmerEnv? item = Interlocked.Exchange(ref _inner, null);
            if (item is not null) pool.Return(item);
        }
    }

    private sealed class CapturingScope(IReadOnlyTxProcessingScope inner, AccountReadVerification verification) : IReadOnlyTxProcessingScope
    {
        private int _disposed;
        public IWorldState WorldState => inner.WorldState;
        public ITransactionProcessor TransactionProcessor => inner.TransactionProcessor;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { verification.Record(inner.WorldState); }
            finally { inner.Dispose(); }
        }
    }
}
