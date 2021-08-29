using System.Collections.Generic;

namespace DistributedTracingSample.Shared
{
    public class MessageWrapper<T>
    {
        public T Input { get; set; }
        public Dictionary<string, string> TraceProperties { get; set; } = new Dictionary<string, string>();

        public MessageWrapper()
        {
        }

        public MessageWrapper(T input)
        {
            Input = input;
        }
    }
}