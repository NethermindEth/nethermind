using System.CommandLine;

namespace Nethermind.Tools.t8n
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
             var outputAllocOpt = new Option<string>("--output.alloc", description: "Output allocations", getDefaultValue: () => "alloc.json");
             var outputBaseDirOpt = new Option<string>("--output.baseDir", description: "Output base directory");
             var outputBodyOpt = new Option<string>("--output.body", description: "Output body");
             var outputResultOpt = new Option<string>("--output.result", description: "Output result", getDefaultValue: () => "result.json");
             var stateChainIdOpt = new Option<int>("--state.chainId", description: "State chain id", getDefaultValue: () => 1);
             var stateForkOpt = new Option<string>("--state.fork", description: "State fork", getDefaultValue: () => "GrayGlacier");
             var stateRewardOpt = new Option<int>("--state.reward", description: "State reward", getDefaultValue: () => 0);
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



            cmd.SetHandler(
                async (context) =>
                {
                    // Note: https://learn.microsoft.com/en-us/dotnet/standard/commandline/model-binding#parameter-binding-more-than-16-options-and-arguments
                    // t8n accepts less options (15) than 16 but command extension methods supports max 8 anyway
                    var traceOpts = new TraceOptions()
                    {
                        Memory = context.ParseResult.GetValueForOption<bool>(traceMemoryOpt),
                        NoMemory = context.ParseResult.GetValueForOption<bool>(traceNoMemoryOpt),
                        NoReturnData = context.ParseResult.GetValueForOption<bool>(traceNoReturnDataOpt),
                        NoStack = context.ParseResult.GetValueForOption<bool>(traceNoStackOpt),
                        ReturnData = context.ParseResult.GetValueForOption<bool>(traceReturnDataOpt),
                    };
                    await T8N.HandleAsync(
                        context.ParseResult.GetValueForOption<string>(inputAllocOpt),
                        context.ParseResult.GetValueForOption<string>(inputEnvOpt),
                        context.ParseResult.GetValueForOption<string>(inputTxsOpt),
                        context.ParseResult.GetValueForOption<string>(outputAllocOpt),
                        context.ParseResult.GetValueForOption<string>(outputBaseDirOpt),
                        context.ParseResult.GetValueForOption<string>(outputBodyOpt),
                        context.ParseResult.GetValueForOption<string>(outputResultOpt),
                        context.ParseResult.GetValueForOption<int>(stateChainIdOpt),
                        context.ParseResult.GetValueForOption<string>(stateForkOpt),
                        context.ParseResult.GetValueForOption<int>(stateRewardOpt),
                        traceOpts
                        );
                });
        }
    }


}
