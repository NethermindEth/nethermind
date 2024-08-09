using System.CommandLine;
using Evm.JsonTypes;
using Evm.T8NTool;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;

namespace Evm
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var rootCmd = new RootCommand();
            rootCmd.Name = "evm";
            ConfigureT8NCommand(ref rootCmd);

            await rootCmd.InvokeAsync(args);
        }

        static void ConfigureT8NCommand(ref RootCommand rootCmd)
        {
            var inputAllocOpt = new Option<string>("--input.alloc", description: "Input allocations", getDefaultValue: () => "alloc.json");
            var inputEnvOpt = new Option<string>("--input.env", description: "Input environment", getDefaultValue: () => "env.json");
            var inputTxsOpt = new Option<string>("--input.txs", description: "Input transactions", getDefaultValue: () => "txs.json");
            var outputAllocOpt = new Option<string>("--output.alloc", description: "Output allocations");
            var outputBaseDirOpt = new Option<string>("--output.basedir", description: "Output base directory");
            var outputBodyOpt = new Option<string>("--output.body", description: "Output body");
            var outputResultOpt = new Option<string>("--output.result", description: "Output result");
            var stateChainIdOpt = new Option<ulong>("--state.chainId", description: "State chain id", getDefaultValue: () => 1);
            var stateForkOpt = new Option<string>("--state.fork", description: "State fork", getDefaultValue: () => "GrayGlacier");
            var stateRewardOpt = new Option<string>("--state.reward", description: "State reward");
            var traceMemoryOpt = new Option<bool>("--trace.memory", description: "Trace memory", getDefaultValue: () => false);
            var traceOpt = new Option<bool>("--trace", description: "Configures the use of the JSON opcode tracer. This tracer emits traces to files as trace-<txIndex>-<txHash>.jsonl", getDefaultValue: () => false);
            var traceNoStackOpt = new Option<bool>("--trace.noStack", description: "Trace no stack", getDefaultValue: () => false);
            var traceReturnDataOpt = new Option<bool>("--trace.returnData", description: "Trace return data", getDefaultValue: () => false);

            var cmd = new Command("t8n", "EVM State Transition command")
            {
                inputAllocOpt,
                inputEnvOpt,
                inputTxsOpt,
                outputAllocOpt,
                outputBaseDirOpt,
                outputBodyOpt,
                outputResultOpt,
                stateChainIdOpt,
                stateForkOpt,
                stateRewardOpt,
                traceOpt,
                traceMemoryOpt,
                traceNoStackOpt,
                traceReturnDataOpt,
            };

            cmd.AddAlias("transition");
            rootCmd.Add(cmd);

            var t8NTool = new T8NTool.T8NTool();

            cmd.SetHandler(
                async (context) =>
                {
                    // Note: https://learn.microsoft.com/en-us/dotnet/standard/commandline/model-binding#parameter-binding-more-than-16-options-and-arguments
                    // t8n accepts less options (15) than 16 but command extension methods supports max 8 anyway
                    var traceOpts = new TraceOptions
                    {
                        IsEnabled = context.ParseResult.GetValueForOption(traceOpt),
                        Memory = context.ParseResult.GetValueForOption(traceMemoryOpt),
                        NoStack = context.ParseResult.GetValueForOption(traceNoStackOpt),
                        ReturnData = context.ParseResult.GetValueForOption(traceReturnDataOpt),
                    };

                    await Task.Run(() =>
                    {
                        var output = t8NTool.Execute(
                            context.ParseResult.GetValueForOption(inputAllocOpt),
                            context.ParseResult.GetValueForOption(inputEnvOpt),
                            context.ParseResult.GetValueForOption(inputTxsOpt),
                            context.ParseResult.GetValueForOption(outputBaseDirOpt),
                            context.ParseResult.GetValueForOption(outputAllocOpt),
                            context.ParseResult.GetValueForOption(outputBodyOpt),
                            context.ParseResult.GetValueForOption(outputResultOpt),
                            context.ParseResult.GetValueForOption(stateChainIdOpt),
                            context.ParseResult.GetValueForOption(stateForkOpt),
                            context.ParseResult.GetValueForOption(stateRewardOpt),
                            traceOpts
                        );
                        Environment.ExitCode = output.ExitCode;
                    });
                });
        }
    }


}
