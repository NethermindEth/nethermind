using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;

namespace Nethermind.Plugin.CLI
{
    class Program
    {
        static int Main(string[] args)
        {
            var className = new Option<String>
                ("--className", "Name of the class");
            className.IsRequired = true;
            className.AddAlias("--c");
            
            var pluginCreator = new Option<String>
                ("--pluginName", "Plugin Name");
            pluginCreator.IsRequired = true;
            pluginCreator.AddAlias("--p");

            var cmd = new RootCommand();
            cmd.AddOption(className);
            cmd.AddOption(pluginCreator);
            
            cmd.Handler = CommandHandler.Create
                <string, string>(ExecuteCliCommand);

            return cmd.Invoke(args);
        }

        static void ExecuteCliCommand(string className, string plugin)
        {
            Console.WriteLine(plugin);
            Console.WriteLine(className);
            OSInfo os = GetOS.GetOperatingSystem();
            switch (os.name)
            {
                case "mac":
                    CreateMacPlugin.CreatePlugin(plugin,className);
                    break;
                case "linux":
                    
                default:
                    Console.WriteLine("Not supported");
                    break;
            }
        }
    }
}
