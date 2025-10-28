// See https://aka.ms/new-console-template for more information


using StatelessExecution;
using System.CommandLine;

RootCommand rootCommand = [];

SetupCli.SetupExecute(rootCommand);
CommandLineConfiguration cli = new(rootCommand);

return await cli.InvokeAsync(args);

