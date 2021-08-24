using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AzureFunctionsDistributedTracingSample.Shared;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry;

namespace AzureFunctionsDistributedTracingSample.AzureFunctions
{
    public static class Functions
    {
        public const string ActivitySourceName = "AzureFunctionsDistributedTracingSample.AzureFunctions";

        [FunctionName("HelloFunction")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var inputObject = context.GetInput<FunctionInput<string>>();
            var parentContext = inputObject.ExtractPropagationContext(m => m.TraceProperties);
            var parentBaggage = JsonConvert.SerializeObject(parentContext.Baggage.GetBaggage());

            Baggage.Current.SetBaggage("test3", "value");
            
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("DurableFunction-FanOut", ActivityKind.Producer, parentContext);

            activity?.SetTag("ParentBaggage", parentBaggage);
            
            var currentBaggage = JsonConvert.SerializeObject(Baggage.Current.GetBaggage());
            activity?.SetTag("CurrentBaggage", currentBaggage);

            var childPropagationContext = parentContext.NewChildPropagationContext();

            var inputModel1 = new FunctionInput<string>(inputObject.Input + " from Tokyo");
            inputModel1.HydrateWithPropagationContext(m => m.TraceProperties, childPropagationContext);

            var inputModel2 = new FunctionInput<string>(inputObject.Input + " from Seattle");
            inputModel2.HydrateWithPropagationContext(m => m.TraceProperties, childPropagationContext);

            var inputModel3 = new FunctionInput<string>(inputObject.Input + " from London");
            inputModel3.HydrateWithPropagationContext(m => m.TraceProperties, childPropagationContext);

            activity?.AddEvent(new ActivityEvent($"Setting up tasks to say hello to {inputObject.Input}"));

            var outputs = new List<Task<string>>
            {
                context.CallActivityAsync<string>("HelloFunction_Hello", inputModel1),
                context.CallActivityAsync<string>("HelloFunction_Hello", inputModel2),
                context.CallActivityAsync<string>("HelloFunction_Hello", inputModel3),

                //Task.Run(() => SayHelloImpl(inputModel1, log)),
                //Task.Run(() => SayHelloImpl(inputModel2 , log)),
                //Task.Run(() => SayHelloImpl(inputModel3, log))
            };

            await Task.WhenAll(outputs);
            activity?.AddEvent(new ActivityEvent($"Tasks completed saying hello to {inputObject.Input}"));

            // returns ["Hello Tokyo!", "Hello Seattle!", "Hello London!"]
            return outputs.Select(t => t.Result).ToList();
        }

        [FunctionName("HelloFunction_Hello")]
        public static string SayHello([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var input = context.GetInput<FunctionInput<string>>();
            return SayHelloImpl(input, log);
        }

        private static string SayHelloImpl(FunctionInput<string> inputObject, ILogger log)
        {
            var parentContext = inputObject.ExtractPropagationContext(m => m.TraceProperties);
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("Function-Consumer", ActivityKind.Consumer, parentContext);

            var currentBaggage = JsonConvert.SerializeObject(Baggage.Current.GetBaggage());
            activity?.SetTag("CurrentBaggage", currentBaggage);

            Thread.Sleep(1000);

            log.LogInformation($"Saying hello to {inputObject.Input}.");
            activity?.AddEvent(new ActivityEvent($"Completed saying hello to {inputObject.Input}"));
            return $"Hello {inputObject.Input}!";
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

            Baggage.Current.SetBaggage("test1", "value");
            Baggage.Current.SetBaggage("test2", "value");

            activity?.SetTag("environment.machineName", Environment.MachineName);
            activity?.SetTag("environment.osVersion", Environment.OSVersion);

            var input = new FunctionInput<string>(username);
            var propagationContext = activity.NewPropagationContext();
            input.HydrateWithPropagationContext(m => m.TraceProperties, propagationContext);

            // Function inputObject comes from the request content.
            string instanceId = await starter.StartNewAsync("HelloFunction", null, input);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}