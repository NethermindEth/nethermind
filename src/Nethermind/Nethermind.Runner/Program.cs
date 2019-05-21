/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Logging;

namespace Nethermind.Runner
{
    public class Program
    {
        private const string FailureString = "Failure";

        public static void Main(string[] args)
        {
            ILogger logger = new NLogLogger("logs.txt");

            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception e)
                    logger.Error(FailureString, e);
                else
                    logger.Error(FailureString + eventArgs.ExceptionObject?.ToString());
            };

            try
            {
                IRunnerApp runner = new RunnerApp(logger);
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