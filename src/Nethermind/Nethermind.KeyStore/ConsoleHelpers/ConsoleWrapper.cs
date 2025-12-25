// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.KeyStore.ConsoleHelpers
{
    /// <summary>
    /// Wrapper around System.Console for dependency injection and testability.
    /// </summary>
    public class ConsoleWrapper : IConsoleWrapper
    {
        /// <summary>
        /// Reads the next character or function key pressed by the user.
        /// </summary>
        /// <param name="intercept">Determines whether to display the pressed key in the console window.</param>
        /// <returns>An object that describes the ConsoleKey constant and Unicode character, if any, that correspond to the pressed console key.</returns>
        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            return Console.ReadKey(intercept);
        }

        /// <summary>
        /// Writes the specified string value to the standard output stream.
        /// </summary>
        /// <param name="message">The value to write.</param>
        public void Write(string message)
        {
            Console.Write(message);
        }

        /// <summary>
        /// Writes the specified string value, followed by the current line terminator, to the standard output stream.
        /// </summary>
        /// <param name="message">The value to write. If null, only the line terminator is written.</param>
        public void WriteLine(string message = null)
        {
            Console.WriteLine(message);
        }
    }
}
