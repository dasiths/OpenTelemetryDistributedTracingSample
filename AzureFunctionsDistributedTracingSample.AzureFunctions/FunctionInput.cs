using System.Collections.Generic;

namespace AzureFunctionsDistributedTracingSample.AzureFunctions
{
    public class FunctionInput<T>
    {
        public T Input { get; set; }
        public Dictionary<string, string> TraceProperties { get; set; } = new Dictionary<string, string>();

        public FunctionInput()
        {
        }

        public FunctionInput(T input)
        {
            Input = input;
        }
    }
}