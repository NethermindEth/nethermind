// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using MachineStateEvents;
using Nethermind.Evm.Lab;
using Nethermind.Evm.Lab.Components;
using Nethermind.Evm.Lab.Interfaces;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.Evm.Lab
{
    public record TraceStateEntry(string Name, MachineState State);
    internal class TraceState : IState<TraceState>
    {
        public TraceState() { }
        public TraceState(TraceStateEntry target, TraceStateEntry subject)
        {
            Traces = (target, subject);
            CompareTraces();
        }
        public TraceState(string targetName, GethLikeTxTrace target, string subjectName, GethLikeTxTrace subject)
        {
            var targetMS = new TraceStateEntry(targetName, new MachineState(target));
            var subjectMS = new TraceStateEntry(subjectName, new MachineState(subject));
            Traces = (targetMS, subjectMS);
            CompareTraces();
        }

        private void CompareTraces()
        {
            var (targetEntries, subjectEntries) = (Traces.target.State.Entries, Traces.subject.State.Entries);
            for (
                DifferenceStartIndex = 0;
                DifferenceStartIndex < Math.Min(targetEntries.Count, subjectEntries.Count)
                && JsonSerializer.Serialize<GethTxTraceEntry>(targetEntries[DifferenceStartIndex]) == JsonSerializer.Serialize<GethTxTraceEntry>(subjectEntries[DifferenceStartIndex]); // Work arround TODO : compare fields 1 by 1
                DifferenceStartIndex++
            ) ;
        }
        IState<TraceState> IState<TraceState>.Initialize(TraceState seed) => seed;
        public (TraceStateEntry target, TraceStateEntry subject) Traces;
        public EventsSink EventsSink { get; } = new EventsSink();
        public int DifferenceStartIndex { get; private set; }
        public TraceState Next()
        {
            Traces.subject?.State?.Next();
            Traces.target?.State?.Next();
            return this;
        }
        public TraceState Previous()
        {
            if (Traces.subject?.State?.Index != Traces.target?.State?.Index)
            {
                var biggerTrace = Traces.subject?.State?.Index > Traces.target?.State?.Index ? Traces.subject?.State : Traces.target?.State;
                biggerTrace?.Previous();
            }
            else
            {
                Traces.subject?.State?.Previous();
                Traces.target?.State?.Previous();
            }
            return this;
        }
        public TraceState Goto(int index)
        {
            Traces.subject?.State?.Goto(index);
            Traces.target?.State?.Goto(index);
            return this;
        }

        public Task<bool> MoveNext()
        {
            if (this.EventsSink.TryDequeueEvent(out var currentEvent))
            {
                lock (this)
                {
                    try
                    {
                        TraceState.Update(this, currentEvent).GetState();
                    }
                    catch (Exception ex)
                    {
                        var dialogView = MainView.ShowError(ex.Message,
                            () =>
                            {
                                this.EventsSink.EnqueueEvent(new Reset());
                            }
                        );
                    }
                }
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public static IState<TraceState> Update(IState<TraceState> state, ActionsBase msg)
        {
            switch (msg)
            {
                case Goto idxMsg:
                    return state.GetState().Goto(idxMsg.index);
                case MoveNext _:
                    return state.GetState().Next();
                case MoveBack _:
                    return state.GetState().Previous();
            }
            return state;
        }
    }
}
