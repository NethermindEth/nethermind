using System;
using Nethermind.RocksDbExtractor.Modules.Data;
using Nethermind.RocksDbExtractor.Modules.Main;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor
{
    static class Program
    {
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Application.Top.Running = false;
                Application.RequestStop();
                Application.Shutdown();
                Console.WriteLine($"There was an error.{Environment.NewLine}{e.ExceptionObject}");
            };
            Application.Init();

            var mainModule = new MainModule();
            mainModule.PathSelected += (_, path) =>
            {
                var dataModule = new DataModule(path);
                var dataModuleWindow = dataModule.Init();
                Application.Top.Add(dataModuleWindow);
                Application.Run(dataModuleWindow);
            };

            Application.Top.Add(mainModule.Init());
            Application.Run();
        }
    }
}
