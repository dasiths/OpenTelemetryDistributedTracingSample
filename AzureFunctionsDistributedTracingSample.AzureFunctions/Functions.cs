using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
        public string TraceId { get; set; }
        public string SpanId { get; set; }
        public string TraceState { get; set; }

        public MyFunctionInput()
        {
        }

        public MyFunctionInput(T inputObject) : this(inputObject,
            Activity.Current == null ? null : Activity.Current.TraceStateString,
            Activity.Current == null ? ActivitySpanId.CreateRandom().ToString() : Activity.Current.SpanId.ToString(),
            Activity.Current == null ? ActivityTraceId.CreateRandom().ToString() : Activity.Current.TraceId.ToString())
        {
        }

        public MyFunctionInput(T inputObject, string traceState, string spanId) : this(inputObject,
            traceState,
            spanId,
            Activity.Current == null ? ActivityTraceId.CreateRandom().ToString() : Activity.Current.TraceId.ToString())
        {
        }

        public MyFunctionInput(T inputObject, string traceState, string spanId, string traceId) : this()
        {
            InputObject = inputObject;
            TraceState = traceState;
            TraceId = traceId;
            SpanId = spanId;
        }

        public ActivityContext GenerateActivityContext() => new ActivityContext(ActivityTraceId.CreateFromString(TraceId),
            ActivitySpanId.CreateFromString(SpanId), ActivityTraceFlags.Recorded, TraceState, true);

        public ActivityContext GenerateNewSpanActivityContext() => new ActivityContext(ActivityTraceId.CreateFromString(TraceId),
            ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, TraceState, true);
    }

    public static class Functions
    {
        private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;
        public const string ActivitySourceName = "AzureFunctionsDistributedTracingSample.AzureFunctions";

        [FunctionName("HelloFunction")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var input = context.GetInput<MyFunctionInput<string>>();
            var activityContext = input.GenerateNewSpanActivityContext();

            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("DurableFunction-FanOut", ActivityKind.Producer, activityContext);
            
            activity?.AddEvent(new ActivityEvent($"Setting up tasks to say hello to {input.InputObject}"));

            AddActivityToHeader(activity, props);

            using var childSpan = source.StartActivity("Wait for task completion");
            var outputs = new List<Task<string>>
            {
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(input.InputObject + " Tokyo")),
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(input.InputObject + " Seattle")),
                //context.CallActivityAsync<string>("HelloFunction_Hello",
                //    new MyFunctionInput<string>(input.InputObject + " London")),

                Task.Run(() => SayHelloImpl(new MyFunctionInput<string>(input.InputObject + " Tokyo"), log)),
                Task.Run(() => SayHelloImpl(new MyFunctionInput<string>(input.InputObject + " Seattle") , log)),
                Task.Run(() => SayHelloImpl(new MyFunctionInput<string>(input.InputObject + " London"), log))
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

        private static void AddActivityToHeader(Activity activity, IBasicProperties props)
        {
            Propagator.Inject(new PropagationContext(activity.Context, Baggage.Current), props, InjectContextIntoHeader);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination_kind", "queue");
            activity?.SetTag("messaging.rabbitmq.queue", "sample");
        }

        private static void InjectContextIntoHeader(IBasicProperties props, string key, string value)
        {
                props.Headers ??= new Dictionary<string, object>();
                props.Headers[key] = value;
        }

        private static string SayHelloImpl(MyFunctionInput<string> input, ILogger log)
        {
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("Function-Consumer", ActivityKind.Consumer,
                input.GenerateNewSpanActivityContext());

            Thread.Sleep(1000);

            log.LogInformation($"Saying hello to {input.InputObject}.");
            activity?.AddEvent(new ActivityEvent($"Completed saying hello to {input.InputObject}"));
            return $"Hello {input.InputObject}!";
        }

        [FunctionName("HelloFunction_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "{username}")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            string username,
            ILogger log)
        {

            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("HttpTrigger-Initiate", ActivityKind.Server);

            var input = new MyFunctionInput<string>(username);

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("HelloFunction", null, input);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}