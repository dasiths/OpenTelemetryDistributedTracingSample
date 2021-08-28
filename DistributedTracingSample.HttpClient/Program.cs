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
        static void Main(string[] args)
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            const string zipkinUri = "http://localhost:9411/api/v2/spans";
            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };

            using var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .AddHttpClientInstrumentation()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("http-client"))
                .AddSource("http-client-test")
                .AddZipkinExporter(o =>
                {
                    o.Endpoint = new Uri(zipkinUri);
                })
                .AddConsoleExporter()
                .Build();
            
            // some sample baggage
            Baggage.Current = Baggage.SetBaggage(new KeyValuePair<string, string>[]
            {
                new ("key1","value1"),
                new ("key2","value2"),
                new ("key3","value3"),
            });

            Console.WriteLine("Press return to trigger the webapi...");
            Console.ReadLine();

            var source = new ActivitySource("http-client-test");
            using (var parent = source.StartActivity("incoming request", ActivityKind.Client))
            {
                Console.WriteLine($"TraceId: {parent?.Context.TraceId}");
                Console.WriteLine($"Baggage: {JsonSerializer.Serialize(Baggage.Current.GetBaggage(), serializerSettings)}");

                var client = new System.Net.Http.HttpClient();
                var result = client.GetStringAsync(@"https://localhost:5001/api/values/sayhello?name=dasith")
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                Console.WriteLine(result);
            }

            Console.WriteLine("Press return to exit...");
            Console.ReadLine();
        }
    }
}
