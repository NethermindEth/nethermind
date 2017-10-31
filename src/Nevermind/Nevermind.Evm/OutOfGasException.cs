using System;

namespace Nevermind.Evm
{
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