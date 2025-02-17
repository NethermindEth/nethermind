using System;
using System.Linq;
using CommandLine;
using Nethermind.Core.Extensions;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.EOFParse.Runner
{
    internal class Program
    {
        public class Options
        {
            [Option('i', "input", Required = false,
                HelpText = "Raw eof input")]
            public string Input { get; set; }

            [Option('x', "stdin", Required = false,
                HelpText =
                    "Interactive testing mode.")]
            public bool Stdin { get; set; }
        }

        public static void Main(params string[] args)
        {
            ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
            if (result is Parsed<Options> options)
                Run(options.Value);
        }

        private static void Run(Options options)
        {
            string input = options.Input;
            if (options.Stdin || input?.Length == 0)
            {
                input = Console.ReadLine();
            }

            while (!string.IsNullOrWhiteSpace(input))
            {
                if (!input.StartsWith('#'))
                {
                    input = new string(input.Where(c => char.IsLetterOrDigit(c)).ToArray());

                    var bytecode = Bytes.FromHexString(input);
                    try
                    {
                        var validationResult = EofValidator.IsValidEof(bytecode, ValidationStrategy.ValidateRuntimeMode,
                            out EofContainer? header);
                        if (validationResult)
                        {
                            var sectionCount = header.Value.CodeSections.Length;
                            var subcontainerCount = header.Value.ContainerSections?.Length ?? 0;
                            var dataCount = header.Value.DataSection.Length;
                            Console.WriteLine($"OK {sectionCount}/{subcontainerCount}/{dataCount}");
                        }
                        else
                        {
                            Console.WriteLine($"err: unknown");
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"err: {e.Message}");
                    }


                    if (!options.Stdin)
                        break;
                }

                input = Console.ReadLine();
            }
        }
    }
}
