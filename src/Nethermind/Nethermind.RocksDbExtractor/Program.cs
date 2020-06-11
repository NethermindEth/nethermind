using Nethermind.RocksDbExtractor.Modules.Data;
using Nethermind.RocksDbExtractor.Modules.Main;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
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
