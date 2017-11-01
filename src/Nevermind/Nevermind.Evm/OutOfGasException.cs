using System;

namespace Nevermind.Evm
{
    public class InvalidJumpDestinationException : Exception
    {
    }

    public class OutOfGasException : Exception
    {
    }

    public class CallDepthException : Exception
    {
    }

    public class StackUnderflowException : Exception
    {
    }

    public class StackOverflowException : Exception
    {
    }
}