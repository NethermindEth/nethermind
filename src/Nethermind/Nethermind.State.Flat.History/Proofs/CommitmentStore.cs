// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class CommitmentStore
{
    private readonly IDb _column;
    private readonly ISortedKeyValueStore _sorted;
    private readonly CommitmentDepthPolicy _policy;
    private readonly int _identityLength;

    public CommitmentStore(IDb column, CommitmentDepthPolicy policy, int identityLength)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column is not ISortedKeyValueStore sorted)
        {
            throw new ArgumentException($"A commitment column must be a {nameof(ISortedKeyValueStore)}.", nameof(column));
        }

        _column = column;
        _sorted = sorted;
        _policy = policy;
        _identityLength = identityLength;
    }

    public CommitmentDepthPolicy Policy => _policy;

    public ulong EpochOf(scoped ReadOnlySpan<byte> prefix, ulong suffix) =>
        CommitmentKeyLayout.IsExactPrefix(prefix, _identityLength) ? _policy.Epoch(suffix) : _policy.EpochOfWindow(suffix);

    public void Write(scoped ReadOnlySpan<byte> prefix, ulong suffix, scoped ReadOnlySpan<byte> value, IWriteBatch batch)
    {
        Span<byte> rowKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteRowKey(rowKey, EpochOf(prefix, suffix), CommitmentKeyLayout.FineTier, prefix, suffix);
        batch.PutSpan(rowKey[..keyLength], value);
    }

    public byte[]? TryGetExact(scoped ReadOnlySpan<byte> prefix, ulong suffix)
    {
        Span<byte> rowKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteRowKey(rowKey, EpochOf(prefix, suffix), CommitmentKeyLayout.FineTier, prefix, suffix);
        return _column.Get(rowKey[..keyLength]);
    }

    public Span<byte> GetExactSpan(scoped ReadOnlySpan<byte> prefix, ulong suffix)
    {
        Span<byte> rowKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteRowKey(rowKey, EpochOf(prefix, suffix), CommitmentKeyLayout.FineTier, prefix, suffix);
        return _column.GetSpan(rowKey[..keyLength]);
    }

    public void Release(Span<byte> value) => _column.DangerousReleaseMemory(value);

    public int ReadStorageTrieDepth(in ValueHash256 accountPath)
    {
        Span<byte> key = stackalloc byte[CommitmentKeyLayout.StorageTrieDepthKeyLength];
        CommitmentKeyLayout.WriteStorageTrieDepthKey(key, accountPath);
        Span<byte> value = _column.GetSpan(key);
        try
        {
            return value.Length == 1 ? value[0] : 0;
        }
        finally
        {
            _column.DangerousReleaseMemory(value);
        }
    }

    public void WriteStorageTrieDepth(in ValueHash256 accountPath, int depth)
    {
        Span<byte> key = stackalloc byte[CommitmentKeyLayout.StorageTrieDepthKeyLength];
        CommitmentKeyLayout.WriteStorageTrieDepthKey(key, accountPath);
        _column.PutSpan(key, [(byte)depth]);
    }

    public RowChain OpenAtOrBelow(scoped ReadOnlySpan<byte> prefix, ulong suffix, ResolutionBudget? budget = null, ulong minEpoch = 0, ulong? startEpoch = null)
    {
        ulong epoch = EpochOf(prefix, suffix);
        if (startEpoch is { } hint && hint < epoch)
        {
            epoch = hint;
            suffix = ulong.MaxValue;
        }

        return new RowChain(_sorted, prefix, suffix, budget, epoch, Math.Min(minEpoch, epoch));
    }

    public RowChain OpenScratchAtOrBelow(scoped ReadOnlySpan<byte> prefix, ulong suffix) => new(_sorted, prefix, suffix, budget: null, epoch: null, minEpoch: 0);

    public void RemoveEpoch(ulong epoch, byte tier)
    {
        Span<byte> lower = stackalloc byte[CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength];
        Span<byte> upper = stackalloc byte[CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength];
        CommitmentKeyLayout.WriteEpochTier(lower, epoch, tier);
        CommitmentKeyLayout.WriteEpochTier(upper, epoch, (byte)(tier + 1));
        IRangeRemovableKeyValueStore removable = (IRangeRemovableKeyValueStore)_column;
        removable.RemoveRange(lower, upper);
        removable.ReclaimRange(lower, upper);
    }

    public sealed class RowChain : IDisposable
    {
        private readonly ISortedKeyValueStore _column;
        private readonly byte[] _prefix;
        private readonly int _prefixLength;
        private readonly ResolutionBudget? _budget;
        private readonly ulong _minEpoch;
        private readonly int _keyLength;
        private ISortedView? _view;
        private ulong? _epoch;

        internal RowChain(ISortedKeyValueStore column, scoped ReadOnlySpan<byte> prefix, ulong suffix, ResolutionBudget? budget, ulong? epoch, ulong minEpoch)
        {
            _column = column;
            _prefix = ArrayPool<byte>.Shared.Rent(prefix.Length);
            prefix.CopyTo(_prefix);
            _prefixLength = prefix.Length;
            _budget = budget;
            _epoch = epoch;
            _minEpoch = minEpoch;
            _keyLength = (epoch is null ? 0 : CommitmentKeyLayout.EpochLength + CommitmentKeyLayout.TierLength) + prefix.Length + CommitmentKeyLayout.SuffixLength;
            Open(suffix);
        }

        public bool MoveNext()
        {
            while (true)
            {
                while (_view!.MoveNext())
                {
                    _budget?.ChargeRow();
                    if (_view.CurrentKey.Length == _keyLength) return true;
                }

                if (_epoch is not { } epoch || epoch <= _minEpoch) return false;

                _view.Dispose();
                _epoch = epoch - 1;
                Open(ulong.MaxValue);
            }
        }

        public ulong CurrentSuffix => CommitmentKeyLayout.ReadSuffix(_view!.CurrentKey);

        public ReadOnlySpan<byte> CurrentValue => _view!.CurrentValue;

        private void Open(ulong suffix)
        {
            ReadOnlySpan<byte> prefix = _prefix.AsSpan(0, _prefixLength);
            Span<byte> seekKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
            Span<byte> upperBound = stackalloc byte[CommitmentKeyLayout.MaxKeyLength + 1];
            int keyLength;
            int upperLength;
            if (_epoch is { } epoch)
            {
                keyLength = CommitmentKeyLayout.WriteRowKey(seekKey, epoch, CommitmentKeyLayout.FineTier, prefix, suffix);
                upperLength = CommitmentKeyLayout.WriteRowUpperBound(upperBound, epoch, CommitmentKeyLayout.FineTier, prefix);
            }
            else
            {
                keyLength = CommitmentKeyLayout.WriteSeekKey(seekKey, prefix, suffix);
                upperLength = CommitmentKeyLayout.WriteUpperBound(upperBound, prefix);
            }

            _view = _column.GetViewBetween(seekKey[..keyLength], upperBound[..upperLength]);
        }

        public void Dispose()
        {
            _view?.Dispose();
            _view = null;
            ArrayPool<byte>.Shared.Return(_prefix);
        }
    }
}
