using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace AzureFunctionsDistributedTracingSample.AzureFunctions
{
    public class MyFunctionInput<T>
    {
        public T InputObject { get; set; }
        public Dictionary<string, string> Properties { get; set; }

        public MyFunctionInput()
        {
        }

        public MyFunctionInput(T inputObject)
        {
            InputObject = inputObject;
        }
    }

    public static class Functions
    {
        // https://www.mytechramblings.com/posts/getting-started-with-opentelemetry-and-dotnet-core/

        private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;
        public const string ActivitySourceName = "AzureFunctionsDistributedTracingSample.AzureFunctions";

        [FunctionName("HelloFunction")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var input = context.GetInput<MyFunctionInput<string>>();

            var parentContext = Propagator.Extract(default, input.Properties, ((objects, s) => ExtractTraceContextFromDto<string>(input, s)));
            Baggage.Current = parentContext.Baggage;

            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("DurableFunction-FanOut", ActivityKind.Producer, parentContext.ActivityContext);

            var inputModel1 = new MyFunctionInput<string>(input.InputObject + " from Tokyo");
            AddActivityToDto(activity, inputModel1);

            var inputModel2 = new MyFunctionInput<string>(input.InputObject + " from Seattle");
            AddActivityToDto(activity, inputModel2);

            var inputModel3 = new MyFunctionInput<string>(input.InputObject + " from London");
            AddActivityToDto(activity, inputModel3);

            activity?.AddEvent(new ActivityEvent($"Setting up tasks to say hello to {input.InputObject}"));

            var outputs = new List<Task<string>>
            {
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(input.InputObject + " Tokyo")),
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(input.InputObject + " Seattle")),
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(input.InputObject + " London")),

                Task.Run(() => SayHelloImpl(inputModel1, log)),
                Task.Run(() => SayHelloImpl(inputModel2 , log)),
                Task.Run(() => SayHelloImpl(inputModel3, log))
            };

            await Task.WhenAll(outputs);
            activity?.AddEvent(new ActivityEvent($"Tasks completed saying hello to {input.InputObject}"));

            // returns ["Hello Tokyo!", "Hello Seattle!", "Hello London!"]
            return outputs.Select(t => t.Result).ToList();
        }

        //[FunctionName("HelloFunction_Hello")]
        //public static string SayHello([ActivityTrigger] IDurableActivityContext context, ILogger log)
        //{
        //    var input = context.GetInput<MyFunctionInput<string>>();
        //    return SayHelloImpl(input, log);
        //}

        private static string SayHelloImpl(MyFunctionInput<string> input, ILogger log)
        {
            var parentContext = Propagator.Extract(default, input.Properties, ((objects, s) => ExtractTraceContextFromDto<string>(input, s)));
            Baggage.Current = parentContext.Baggage;

            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("Function-Consumer", ActivityKind.Consumer, parentContext.ActivityContext);

            Thread.Sleep(1000);

            log.LogInformation($"Saying hello to {input.InputObject}.");
            activity?.AddEvent(new ActivityEvent($"Completed saying hello to {input.InputObject}"));
            return $"Hello {input.InputObject}!";
        }

        private static void AddActivityToDto<T>(Activity activity, MyFunctionInput<T> props)
        {
            Propagator.Inject(new PropagationContext(activity.Context, Baggage.Current), props, InjectContextIntoHeader);
        }

        private static void InjectContextIntoHeader<T>(MyFunctionInput<T> props, string key, string value)
        {
            props.Properties ??= new Dictionary<string, string>();
            props.Properties[key] = value;
            
            // setup baggage in dictionary here
        }

        //Extract the Activity from the message header
        private static IEnumerable<string> ExtractTraceContextFromDto<T>(MyFunctionInput<T> props, string key)
        {
            if (props.Properties.TryGetValue(key, out var value))
            {
                return new[] { value };
            }

            // detect if key is baggage and set to Baggage.Current here

            return Enumerable.Empty<string>();
        }
        
        [FunctionName("HelloFunction_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "{username}")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            string username,
            ILogger log)
        {

            Baggage.Current.SetBaggage("test baggage", "value");
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("HttpTrigger-Initiate", ActivityKind.Server);
            
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination_kind", "queue");
            activity?.SetTag("messaging.rabbitmq.queue", "sample");

            var input = new MyFunctionInput<string>(username);
            AddActivityToDto(activity, input);

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("HelloFunction", null, input);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}