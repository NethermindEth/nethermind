using System;
using System.Numerics;
using Nethermind.Core.Logging;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Store;

namespace Nethermind.MrWolf
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Jimmie, lead the way. Boys, get to work.");
            Console.WriteLine("1) Delete chain levels beyond X");

            while (true)
            {
                string choice = Console.ReadLine();
                if (choice != "1)")
                {
                    Console.WriteLine("Come again?");
                }
                else
                {
                    break;
                }
            }
            
            Console.WriteLine("Now, provide a level number, level that is beyond the current head reported by the client:");
            Console.WriteLine("You should see something like: ");
            Console.WriteLine("|Block tree initialized, last processed is 6781580 (d36623), best queued is 6781580, best known is 6785054");
            Console.WriteLine("In such case you would provide: 6781581");
            
            if (GetNumber("from", out BigInteger numberFromParsed)) return;
            if (GetNumber("to", out BigInteger numberToParsed)) return;

            Console.WriteLine($"Are you sure you want to delete chain levels from {numberFromParsed} to {numberToParsed}");
            string confirmation = Console.ReadLine();
            if (confirmation == "yes")
            {
                Console.WriteLine("OK, give me the DB location:");
                string dbPath = Console.ReadLine();
                if (!(dbPath?.EndsWith("blockInfos") ?? false))
                {
                    Console.WriteLine("DB location should end with blockInfos");
                    return;
                }
                
                IDb dbOnTheRocks = new DbOnTheRocks(dbPath, DbConfig.Default, new OneLoggerLogManager(new SimpleConsoleLogger()));

                for (BigInteger current = numberFromParsed; current <= numberToParsed; current++)
                {
                    byte[] bytes = dbOnTheRocks.Get(current);
                    if (bytes != null)
                    {
                        Console.Write($"Deleting {current}:");
                        dbOnTheRocks.Delete(current);
                    }
                    else
                    {
                        Console.Write($"Skipping {current} as it did not exist");
                    }
                }
            }
        }

        private static bool GetNumber(string description, out BigInteger numberFromParsed)
        {
            Console.WriteLine($"Now, what is the '{description}' number?");
            string numberFrom = Console.ReadLine();
            if (numberFrom == "6781581")
            {
                Console.WriteLine("Are you sure you did not just copy the example? Are you tired?");
            }

            Console.WriteLine($"Confirm the '{description}' number:");
            string numberFromConfirmation = Console.ReadLine();

            if (numberFrom == numberFromConfirmation && numberFrom != null)
            {
                numberFromParsed = BigInteger.Parse(numberFromConfirmation);
            }
            else
            {
                Console.WriteLine("Numbers do not match!");
                return true;
            }

            return false;
        }
    }
}