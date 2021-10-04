using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DistributedTracingSample.HttpClient
{
    class Program
    {
        private const string ZipkinUri = "http://localhost:9411/api/v2/spans";
        private const string ActivitySourceName = "http-client-test-source";
        private const string ServiceName = "http-client";
        
        static void Main(string[] args)
        {
            // Use the W3C trace context https://www.w3.org/TR/trace-context/
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            
            // setup the trace provider
            using var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                .AddSource(ActivitySourceName) // Opting in to any spans coming from this source
                .AddHttpClientInstrumentation() // Opting in for sql client instrumentation from OpenTelemetry.Instrumentation.Http nuget library
                .AddZipkinExporter(o =>
                {
                    o.Endpoint = new Uri(ZipkinUri); // Asking OpenTelemetry collector to export traces to Zipkin via OpenTelemetry.Exporter.Zipkin nuget library
                })
                .AddConsoleExporter() // also export to console via OpenTelemetry.Exporter.Console nuget library
                .Build();

            Console.WriteLine("Press return to trigger the webapi...");
            Console.ReadLine();

            do
            {
                CallWebApi();
                Console.WriteLine("Do you want to trigger the api again (Y/N)? ");
                
            } while (Console.ReadKey().KeyChar.ToString().ToLower() == "y");

            Console.WriteLine("\nPress return to exit...");
            Console.ReadLine();
        }

        private static void CallWebApi()
        {
            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };

            // create new span via .NET Activity API.
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("outgoing request", ActivityKind.Client);

            // use baggage to send additional data with the propagation context
            Baggage.Current = Baggage.SetBaggage(new KeyValuePair<string, string>[]
            {
                new("key1", "value1"),
                new("key2", "value2"),
                new("key3", "value3"),
            });

            // use tags to add additional data about the span to this trace
            activity?.SetTag("environment.machineName", Environment.MachineName);
            activity?.SetTag("environment.osVersion", Environment.OSVersion);

            Console.WriteLine($"TraceId: {activity?.Context.TraceId}");
            Console.WriteLine($"Baggage: {JsonSerializer.Serialize(Baggage.Current.GetBaggage(), serializerSettings)}");

            // use events to log things
            activity?.AddEvent(new ActivityEvent("This is something I'm logging"));

            // make the http calls to external services.
            // HttpClient is instrumented via OpenTelemetry.Instrumentation.Http library
            
            var client = new System.Net.Http.HttpClient();
            var result = client.GetStringAsync(@"https://localhost:5001/api/values/sayhello?name=dasith")
                .ConfigureAwait(false).GetAwaiter().GetResult();

            Console.WriteLine(result);
        }
    }
}
