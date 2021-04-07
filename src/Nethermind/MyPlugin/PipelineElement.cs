using System;
using Nethermind.Core;
using Nethermind.Pipeline;

namespace MyPlugin
{
    public class CheckIfContractCreationElement<T> : IPipelineElement<T> where T : Transaction
    {
        public Action<T> Emit { private get; set; }

        public void SubscribeToData(T data)
        {
            if(Emit == null)
            {
                return;
            }

            if(data.IsContractCreation)
            {
                Emit(data);
            }
        }
    }
}