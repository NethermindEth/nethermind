using System;

/// <summary>Runs the harness inside the host process before its Main, then exits.</summary>
/// <remarks>Inert unless <c>RPCPOC_ARGS</c> is set, so the host behaves normally otherwise.</remarks>
internal class StartupHook
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Initialize()
    {
        string args = Environment.GetEnvironmentVariable("RPCPOC_ARGS");
        if (string.IsNullOrEmpty(args)) return;

        // The harness compiles hot code at run time, by the plugin's Legacy rules, unless NETHERMIND_ILEVM_MODE names
        // others; set before anything reads the plugin's settings.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MODE")))
            Environment.SetEnvironmentVariable("NETHERMIND_ILEVM_MODE", "Legacy");

        // Windows power throttling (EcoQoS) can confine a background console process to the efficiency cores for a whole
        // run, which halves its throughput in some runs and not others; opt out so every run gets the same CPUs.
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("RPCPOC_THROTTLE") != "1")
        {
            PowerThrottlingState state = new() { Version = 1, ControlMask = 1, StateMask = 0 };
            bool ok = SetProcessInformation(GetCurrentProcess(), 4, ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<PowerThrottlingState>());
            Console.WriteLine($"power throttling opt-out: {(ok ? "ok" : "failed")}");
        }

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
        // for its plugins directory, or from RPCPOC_PLUGIN_DIR, the build this harness was compiled against.
        string pluginDir = Environment.GetEnvironmentVariable("RPCPOC_PLUGIN_DIR");
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (!string.IsNullOrEmpty(pluginDir) && name.Name == "Nethermind.IlEvm")
                path = System.IO.Path.Combine(pluginDir, name.Name + ".dll");
            return System.IO.File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        int code;
        try
        {
            code = Environment.GetEnvironmentVariable("RPCPOC_MODE") switch
            {
                "cold" => RpcReplay.ColdLoad.Run(args.Split('|')).GetAwaiter().GetResult(),
                "ramp" => RpcReplay.ColdRamp.Run(args.Split('|')).GetAwaiter().GetResult(),
                _ => RpcReplay.Harness.Run(args.Split('|')).GetAwaiter().GetResult(),
            };
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
