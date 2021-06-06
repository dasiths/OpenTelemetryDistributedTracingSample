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
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using JsonConverter = System.Text.Json.Serialization.JsonConverter;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace AzureFunctionsDistributedTracingSample.AzureFunctions
{
    public class MyFunctionInput<T>
    {
        public T Input { get; set; }
        public Dictionary<string, string> TraceProperties { get; set; } = new Dictionary<string, string>();

        public MyFunctionInput()
        {
        }

        public MyFunctionInput(T input)
        {
            Input = input;
        }
    }

    public static class Functions
    {
        // https://www.mytechramblings.com/posts/getting-started-with-opentelemetry-and-dotnet-core/

        private static readonly TextMapPropagator TextMapPropagator = Propagators.DefaultTextMapPropagator;

        public const string ActivitySourceName = "AzureFunctionsDistributedTracingSample.AzureFunctions";
        public const string TraceContextBaggageKeyName = "baggage";

        [FunctionName("HelloFunction")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var inputObject = context.GetInput<MyFunctionInput<string>>();
            var parentContext = ExtractTraceContext(inputObject.TraceProperties);

            using var source = new ActivitySource(ActivitySourceName);
            using var activity = StartActivity(source, "DurableFunction-FanOut", ActivityKind.Producer, parentContext);

            activity?.SetTag("ParentBaggage", JsonConvert.SerializeObject(parentContext.Baggage.GetBaggage()));
            Baggage.Current.SetBaggage("test3", "value");

            var propagationContext = CreateNewPropagationContext(parentContext);

            var inputModel1 = new MyFunctionInput<string>(inputObject.Input + " from Tokyo");
            HydrateWithPropagationContext(propagationContext, inputModel1.TraceProperties);

            var inputModel2 = new MyFunctionInput<string>(inputObject.Input + " from Seattle");
            HydrateWithPropagationContext(propagationContext, inputModel2.TraceProperties);

            var inputModel3 = new MyFunctionInput<string>(inputObject.Input + " from London");
            HydrateWithPropagationContext(propagationContext, inputModel3.TraceProperties);

            activity?.AddEvent(new ActivityEvent($"Setting up tasks to say hello to {inputObject.Input}"));

            var outputs = new List<Task<string>>
            {
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(inputObject.Input + " Tokyo")),
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(inputObject.Input + " Seattle")),
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(inputObject.Input + " London")),

                Task.Run(() => SayHelloImpl(inputModel1, log)),
                Task.Run(() => SayHelloImpl(inputModel2 , log)),
                Task.Run(() => SayHelloImpl(inputModel3, log))
            };

            await Task.WhenAll(outputs);
            activity?.AddEvent(new ActivityEvent($"Tasks completed saying hello to {inputObject.Input}"));

            // returns ["Hello Tokyo!", "Hello Seattle!", "Hello London!"]
            return outputs.Select(t => t.Result).ToList();
        }

        //[FunctionName("HelloFunction_Hello")]
        //public static string SayHello([ActivityTrigger] IDurableActivityContext context, ILogger log)
        //{
        //    var input = context.GetInput<MyFunctionInput<string>>();
        //    return SayHelloImpl(input, log);
        //}

        private static string SayHelloImpl(MyFunctionInput<string> inputObject, ILogger log)
        {
            var parentContext = ExtractTraceContext(inputObject.TraceProperties);
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = StartActivity(source, "Function-Consumer", ActivityKind.Consumer, parentContext);

            activity?.SetTag("CurrentBaggage", JsonConvert.SerializeObject(parentContext.Baggage.GetBaggage()));

            Thread.Sleep(1000);

            log.LogInformation($"Saying hello to {inputObject.Input}.");
            activity?.AddEvent(new ActivityEvent($"Completed saying hello to {inputObject.Input}"));
            return $"Hello {inputObject.Input}!";
        }

        private static PropagationContext CreateNewPropagationContext(Activity activity)
        {
            var currentBaggage = new Baggage()
                .SetBaggage(Baggage.Current.GetBaggage())
                .SetBaggage(activity.Baggage);

            return new PropagationContext(activity.Context, currentBaggage);
        }

        private static PropagationContext CreateNewPropagationContext(PropagationContext parentContext)
        {
            var currentBaggage = new Baggage()
                    .SetBaggage(Baggage.Current.GetBaggage())
                    .SetBaggage(parentContext.Baggage.GetBaggage());

            if (Activity.Current != null)
            {
                currentBaggage = currentBaggage.SetBaggage(Activity.Current.Baggage);
            }

            return new PropagationContext(parentContext.ActivityContext, currentBaggage);
        }

        private static void HydrateWithPropagationContext(PropagationContext context, Dictionary<string, string> traceProperties)
        {
            TextMapPropagator.Inject(context, traceProperties,
                (properties, key, value) =>
                {
                    properties[key] = value;
                });
        }

        //Extract the Activity from the message header
        private static PropagationContext ExtractTraceContext(Dictionary<string, string> properties)
        {
            var propagationContext = TextMapPropagator.Extract(default, properties, (props, key) =>
            {
                if (props.TryGetValue(key, out var value))
                {
                    return new[] { value };
                }

                return Enumerable.Empty<string>();
            });

            return propagationContext;
        }

        private static Activity StartActivity(ActivitySource source, string activityName, ActivityKind kind)
        {
            return source.StartActivity(activityName, kind);
        }

        private static Activity StartActivity(ActivitySource source, string activityName, ActivityKind kind, PropagationContext propagationContext)
        {
           var activity = source.StartActivity(activityName, kind, propagationContext.ActivityContext);
           
           foreach (var (key, value) in propagationContext.Baggage)
           {
               activity?.AddBaggage(key, value);
           }

           return activity;
        }

        [FunctionName("HelloFunction_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "{username}")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            string username,
            ILogger log)
        {
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = StartActivity(source, "HttpTrigger-Initiate", ActivityKind.Server);

            Baggage.Current.SetBaggage("test1", "value");
            Baggage.Current.SetBaggage("test2", "value");

            activity?.SetTag("environment.machineName", Environment.MachineName);
            activity?.SetTag("environment.osVersion", Environment.OSVersion);

            var input = new MyFunctionInput<string>(username);
            var propagationContext = CreateNewPropagationContext(activity);
            HydrateWithPropagationContext(propagationContext, input.TraceProperties);

            // Function inputObject comes from the request content.
            string instanceId = await starter.StartNewAsync("HelloFunction", null, input);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}