// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Lab;
using Nethermind.Evm.Lab.Components;
using Nethermind.Evm.Tracing.Debugger;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Specs.Forks;
using Terminal.Gui;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Lab.Interfaces;

// TODO : Fix breakpoint setting on code section > 0 (requires alot of stuff)
// TODO : Handle Storage UI
// TODO : Refactor code or (switch completely to MVVM or Imgui like style)


#if false

byte[] bytecode = Nethermind.Core.Extensions.Bytes.FromHexString("716860176017601701500060005260096017f3600052601260006000f0");
EthereumRestrictedInstance context = new(Cancun.Instance);
DebugTracer tracer = new DebugTracer(new GethLikeTxTracer(GethTraceOptions.Default))
{
    IsStepByStepModeOn = true,
};

var vmTask = Task.Run(() => context.Execute(tracer, long.MaxValue, bytecode));

static bool ParseCommand(string commandLine, [NotNullWhen(true)] out (int lineBreak, Func<EvmState, bool>? conditoon)? result)
{
    // [-line <LineNumber> [-cond <conditionExpr>]]
    if(String.IsNullOrWhiteSpace(commandLine))
    {
        result = null;
        return false;
    }

    try
    {
        commandLine = commandLine.Trim();
        if (!commandLine.StartsWith("-line")) throw new ArgumentException();
        commandLine = commandLine.Substring("-line".Length + 1);
        int lineArgEndOffset = commandLine.IndexOf(" ");
        int lineBreak = Int32.Parse(commandLine.Substring(0, lineArgEndOffset));
        Func<EvmState, bool>? condition = null;
        if(commandLine.Length > lineArgEndOffset)
        {
            commandLine = commandLine.Substring(++lineArgEndOffset);
            if (!commandLine.StartsWith("-cond")) throw new ArgumentException();
            commandLine = commandLine.Substring("-cond".Length + 1);
            int condArgStartOffset = commandLine.IndexOf("\"") + 1;
            int condArgEndOffset = commandLine.IndexOf("\"", condArgStartOffset);
            string conditionString = commandLine[condArgStartOffset..condArgEndOffset];
            condition = CSharpScript.EvaluateAsync<Func<EvmState, bool>>(conditionString, ScriptOptions.Default.WithReferences(typeof(Nethermind.Evm.EvmState).Assembly)).Result;
        }
        result = (lineBreak, condition);
        return true;
    } catch
    {
        result = null;
        return false;
    }
}

while (!vmTask.IsCompleted)
{
    if(tracer.CanReadState)
    {
        Console.WriteLine(
            JsonSerializer.Serialize<EvmState>(tracer.CurrentState, new JsonSerializerOptions
            {
                WriteIndented = true,
            })
        );

        if(ParseCommand(Console.ReadLine(), out (int lineBreak, Func<EvmState, bool>? conditoon)? BreakPoint))
        {
            tracer.IsStepByStepModeOn = false;
            tracer.SetBreakPoint(BreakPoint.Value.lineBreak, BreakPoint.Value.conditoon);
        }

        tracer.MoveNext();
    }
}
#else
GlobalState.initialCmdArgument = args.Length == 0 ? "0x716860176017601701500060005260096017f36000526012600e6000f0" : args[0];
var mainView = new MainView();
mainView.Run(mainView.State);
#endif
