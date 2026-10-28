// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

public static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The working memory of one fold on one thread: the groups it loaded, its group writers and its frames' scratch.</summary>
    /// <remarks>
    /// Working states are pooled, unless grown past <see cref="MaxPooledPendingGroups"/> pending groups; a returned one releases its
    /// loaded records and pending groups and keeps its writers, with their scratch, and its buffers for the next fold.
    /// </remarks>
    public sealed partial class FoldWorkingState : IDisposable
    {
        private const int MaxPooled = 64;
        /// <summary>How many writers a state keeps, enough for the deepest fold's frames.</summary>
        private const int MaxPooledWriters = 64;

        private static readonly ConcurrentQueue<FoldWorkingState> Pool = new();

        private readonly Stack<PbtNodeGroupWriter<TPath>> _writers = new();
        private readonly Stack<(byte[] Encodings, SlotNode[] Nodes)> _foldedAhead = new();

        /// <summary>The groups loaded from the store, readable until the state is returned.</summary>
        public PbtLoadedRecordBuffer Loaded { get; } = new();

        /// <summary>The slot folds of a frame fanning out, which never nests on one state, as its runs fold on spare states.</summary>
        public SortedSlotFold[] Folds { get; } = new SortedSlotFold[PbtTwoLevelGroupGeometry.BoundarySlots];

        /// <summary>The ends of the runs <see cref="Folds"/> is folded in.</summary>
        public int[] RunEnds { get; } = new int[PbtTwoLevelGroupGeometry.BoundarySlots];

        public static FoldWorkingState Rent() => Pool.TryDequeue(out FoldWorkingState? state) ? state : new FoldWorkingState();

        public static void Return(FoldWorkingState state)
        {
            if (Pool.Count < MaxPooled && state.IsPoolable)
            {
                state.Loaded.Clear();
                state.ClearPending();
                Pool.Enqueue(state);
            }
            else
            {
                state.Dispose();
            }
        }

        /// <summary>A writer of an empty group at <paramref name="bitDepth"/>, until <see cref="ReturnWriter"/>.</summary>
        public PbtNodeGroupWriter<TPath> RentWriter(int bitDepth)
        {
            if (!_writers.TryPop(out PbtNodeGroupWriter<TPath>? writer)) return new PbtNodeGroupWriter<TPath>(bitDepth);

            writer.Reset(bitDepth);
            return writer;
        }

        public void ReturnWriter(PbtNodeGroupWriter<TPath> writer)
        {
            if (_writers.Count < MaxPooledWriters) _writers.Push(writer);
            else writer.Dispose();
        }

        /// <summary>Buffers for the nodes of the boundary slots a frame folds ahead of its walk, <see cref="MaxNodeLength"/> bytes per slot, until <see cref="ReturnFoldedAhead"/>.</summary>
        public void RentFoldedAhead(out byte[] encodings, out SlotNode[] nodes)
        {
            if (!_foldedAhead.TryPop(out (byte[] Encodings, SlotNode[] Nodes) buffers))
                buffers = (new byte[PbtTwoLevelGroupGeometry.BoundarySlots * MaxNodeLength], new SlotNode[PbtTwoLevelGroupGeometry.BoundarySlots]);
            (encodings, nodes) = buffers;
        }

        public void ReturnFoldedAhead(byte[] encodings, SlotNode[] nodes) => _foldedAhead.Push((encodings, nodes));

        public void Dispose()
        {
            Loaded.Dispose();
            foreach (PbtNodeGroupWriter<TPath> writer in _writers) writer.Dispose();
            _writers.Clear();
            DisposePending();
        }
    }

    /// <summary>The working states of one fold's parallel runs, reused by later runs and returned to the pool once the fold ends.</summary>
    /// <remarks>A reused working state keeps the records it loaded, so every record loaded in the fold stays readable until it ends.</remarks>
    public sealed class SpareWorkingStates : IDisposable
    {
        private readonly ConcurrentBag<FoldWorkingState> _states = [];

        public FoldWorkingState Rent() => _states.TryTake(out FoldWorkingState? state) ? state : FoldWorkingState.Rent();

        public void Return(FoldWorkingState state) => _states.Add(state);

        public void Dispose()
        {
            foreach (FoldWorkingState state in _states) FoldWorkingState.Return(state);
        }
    }
}
