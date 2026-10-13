using System;

/// <summary>Runs the harness inside the host process before its Main, then exits.</summary>
/// <remarks>Inert unless <c>RPCPOC_ARGS</c> is set, so the host behaves normally otherwise.</remarks>
internal class StartupHook
{
    public static void Initialize()
    {
        string args = Environment.GetEnvironmentVariable("RPCPOC_ARGS");
        if (string.IsNullOrEmpty(args)) return;

        // Pinned to chosen cores (a hex mask) at high priority, so a hybrid CPU's efficiency cores and the scheduler do not move timings.
        string affinity = Environment.GetEnvironmentVariable("RPCPOC_AFFINITY");
        if (!string.IsNullOrEmpty(affinity))
        {
            using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            self.ProcessorAffinity = (IntPtr)Convert.ToInt64(affinity, 16);
            self.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            Console.WriteLine($"affinity 0x{affinity}, priority high");
        }

        // The plugin is not in the host's dependency manifest; resolve it next to the host, as the plugin loader does
        // for its plugins directory.
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += static (context, name) =>
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return System.IO.File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        int code;
        try
        {
            code = RpcReplay.Harness.Run(args.Split('|')).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            code = 3;
        }

        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(code);
    }
}
