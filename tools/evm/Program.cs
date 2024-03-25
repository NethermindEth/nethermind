using System.CommandLine;
using Evm.JsonTypes;
using Evm.T8NTool;
using Nethermind.Core.Exceptions;

namespace Evm
{
    public class TraceOptions
    {
        public bool Memory { get; set; }
        public bool NoMemory { get; set; }
        public bool NoReturnData { get; set; }
        public bool NoStack { get; set; }
        public bool ReturnData { get; set; }
    }

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
             var stateChainIdOpt = new Option<int>("--state.chainId", description: "State chain id", getDefaultValue: () => 1);
             var stateForkOpt = new Option<string>("--state.fork", description: "State fork", getDefaultValue: () => "GrayGlacier");
             var stateRewardOpt = new Option<string>("--state.reward", description: "State reward");
             var traceMemoryOpt = new Option<bool>("--trace.memory", description: "Trace memory", getDefaultValue: () => false);
             var traceNoMemoryOpt = new Option<bool>("--trace.noMemory", description: "Trace no memory", getDefaultValue: () => true);
             var traceNoReturnDataOpt = new Option<bool>("--trace.noReturnData", description: "Trace no return data", getDefaultValue: () => true);
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
                traceMemoryOpt,
                traceNoMemoryOpt,
                traceNoReturnDataOpt,
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
                    var traceOpts = new TraceOptions()
                    {
                        Memory = context.ParseResult.GetValueForOption(traceMemoryOpt),
                        NoMemory = context.ParseResult.GetValueForOption(traceNoMemoryOpt),
                        NoReturnData = context.ParseResult.GetValueForOption(traceNoReturnDataOpt),
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
                            traceOpts.Memory,
                            traceOpts.NoMemory,
                            traceOpts.NoReturnData,
                            traceOpts.NoStack,
                            traceOpts.ReturnData
                        );
                        Environment.ExitCode = output.ExitCode;
                    });
                });
        }
    }


}
