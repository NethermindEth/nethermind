using System;
using System.Security;

namespace Nevermind.Core.Crypto
{
    public class ConsoleUtils
    {
        public static SecureString ReadSecret(string secretDisplayName)
        {
            Console.WriteLine($"{secretDisplayName}:");
            SecureString secureString = new SecureString();
            do
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (secureString.Length > 0)
                    {
                        secureString.RemoveAt(secureString.Length - 1);
                        Console.Write("\b \b");
                    }

                    continue;
                }

                secureString.AppendChar(key.KeyChar);
                Console.Write("*");
            } while (true);

            Console.WriteLine();

            secureString.MakeReadOnly();
            return secureString;
        }
    }
}