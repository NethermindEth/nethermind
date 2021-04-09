using System;

namespace Nethermind.Pipeline
{
    /// <summary> 
    /// Marker interface which makes storing elements in <see cref="IPipeline"/> easier.
    /// </summary>
    public interface IPipelineElement
    {
    }

    /// <summary>
    /// Represents an element of <see cref="IPipeline"/> wich emits data of <typeparamref name="TOut"/> type. 
    /// Used as a first element in <see cref="IPipeline"/>.
    /// </summary>
    public interface IPipelineElement<TOut> : IPipelineElement
    {
        /// <summary>
        /// Encapsulation of a method with single parameter of type <typeparamref name="TOut"/> which is emited data from previous element. 
        /// Used once data inside of element is ready to be passed to the next element.
        /// </summary>
        Action<TOut> Emit { set; }
    }

    /// <summary>
    /// Represents an inner element of <see cref="IPipeline"/> which subscribes to data from previous element of <typeparamref name="TIn"/> type,
    /// and emits data of <typeparamref name="TOut"/> type to the next element. 
    /// </summary>
    public interface IPipelineElement<TIn, TOut> : IPipelineElement<TOut>
    {
        /// <summary>
        /// Method which is supposed to be encapsulated in <see cref="IPipelineElement{TOut}.Emit"/> from previous element in pipeline
        /// and receive data from it of <typeparamref name="TIn"/> type. 
        /// <summary> 
        void SubscribeToData(TIn data);
    }
}