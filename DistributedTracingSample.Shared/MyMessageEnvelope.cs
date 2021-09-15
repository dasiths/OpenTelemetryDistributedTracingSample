using System.Collections.Generic;

namespace DistributedTracingSample.Shared
{
    /// <summary>
    /// This is a sample wrapper to store a message and the span context information
    /// </summary>
    /// <typeparam name="T">The type of message this enveloper with contain</typeparam>
    public class MyMessageEnvelope<T>
    {
        public T Input { get; set; }
        public Dictionary<string, string> TraceContext { get; set; } = new Dictionary<string, string>();

        public MyMessageEnvelope()
        {
        }

        public MyMessageEnvelope(T input)
        {
            Input = input;
        }
    }
}