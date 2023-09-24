// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Evm.Lab;
using Nethermind.Evm.Lab.Interfaces;



namespace GlobalStateEvents.Actions
{
    public record AddPage<T>(string name, T? customState = default, bool isExternalTrace = false) : ActionsBase
        where T : new();
    public record RemovePage(int index) : ActionsBase;
    public record Reset : ActionsBase;

}

namespace Nethermind.Evm.Lab
{

    internal class GlobalState : IState<GlobalState>
    {
        public static string initialCmdArgument;
        public EventsSink EventsSink { get; } = new EventsSink();

        public List<IStateObject> MachineStates = new List<IStateObject>();
        IState<GlobalState> IState<GlobalState>.Initialize(GlobalState seed) => seed;

        public Task<bool> MoveNext()
        {
            throw new UnreachableException();
        }

        public int SelectedState { get; set; }
    }
}
