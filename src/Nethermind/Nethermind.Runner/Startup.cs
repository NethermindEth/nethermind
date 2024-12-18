// ... in startup configuration
services.AddSingleton<NethermindDataDirectoryProvider>();

// During initialization
var pathProvider = serviceProvider.GetRequiredService<NethermindDataDirectoryProvider>();

// Use the default base path for database and logs
string dbPath = pathProvider.GetDbPath(DbNames.Storage);
string logsPath = pathProvider.GetLogsPath();


Directory.CreateDirectory(dbPath);
Directory.CreateDirectory(logsPath);
