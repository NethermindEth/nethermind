

using System;
using Nethermind.Logging;
using Nethermind.Runner;

namespace ChainLoader
{
    public class Program
    {
        private const string FailureString = "Failure";

        public static void Main(string[] args)
        {
            ILogger logger = new NLogLogger("loadlog.txt");

            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception e)
                    logger.Error(FailureString, e);
                else
                    logger.Error(FailureString + eventArgs.ExceptionObject?.ToString());
            };

            try
            {
                IRunnerApp runner = new ChainLoaderApp(logger);
                runner.Run(args);
                return;
            }
            catch (AggregateException e)
            {
                logger.Error(FailureString, e.InnerException);
            }
            catch (Exception e)
            {
                logger.Error(FailureString, e);
            }

            Console.WriteLine("Press RETURN to exit.");
            Console.ReadLine();
        }
    }
}