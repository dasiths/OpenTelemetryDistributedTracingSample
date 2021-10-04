using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DistributedTracingSample.Shared;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;

namespace DistributedTracingSample.Messaging.Publisher
{
    class Program
    {
        private const string ZipkinUri = "http://localhost:9411/api/v2/spans";
        private const string ActivitySourceName = "message-publisher-test-source";
        private const string QueueName = "distributed-tracing-sample-queue";
        private const string ServiceName = "message-publisher";

        static void Main(string[] args)
        {
            // Use the W3C trace context https://www.w3.org/TR/trace-context/
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            // setup the trace provider
            using var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                .AddSource(ActivitySourceName) // Opting in to any spans coming from this source
                .AddZipkinExporter(o =>
                {
                    o.Endpoint = new Uri(ZipkinUri); // Asking OpenTelemetry collector to export traces to Zipkin via OpenTelemetry.Exporter.Zipkin nuget library
                })
                .AddConsoleExporter() // Also export to console via OpenTelemetry.Exporter.Console nuget library
                .Build();

            // create RabbitMQ connection
            var factory = new ConnectionFactory() { HostName = "localhost" };
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            
            channel.QueueDeclare(queue: QueueName,
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            Console.WriteLine("Press return to send the message...");
            Console.ReadLine();

            do
            {
                SendMessage(channel);
                Console.WriteLine("Do you want to trigger the api again (Y/N)? ");
                
            } while (Console.ReadKey().KeyChar.ToString().ToLower() == "y");

            Console.WriteLine("\nPress return to exit...");
            Console.ReadLine();
        }

        private static void SendMessage(IModel channel)
        {
            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };

            // create new span via .NET Activity API.
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("outgoing message", ActivityKind.Producer);

            // use baggage to send additional data with the propagation context
            Baggage.Current = Baggage.SetBaggage(new KeyValuePair<string, string>[]
            {
                new("key1", "value1"),
                new("key2", "value2"),
                new("key3", "value3"),
            });

            Console.WriteLine($"TraceId: {activity?.Context.TraceId}");
            Console.WriteLine($"Baggage: {JsonSerializer.Serialize(Baggage.Current.GetBaggage(), serializerSettings)}");

            // use tags to trace additional data about the span
            activity?.SetTag("environment.machineName", Environment.MachineName);
            activity?.SetTag("environment.osVersion", Environment.OSVersion);

            // prepare model by wrapping your message in our custom message envelope
            var message = "Hello World!";
            var model = new MyMessageEnvelope<string>(message);

            // create the OpenTelemetry propagation context and hydrate the model with it
            // we are using the helper class for CreatePropagationContext() and HydrateWithPropagationContext() methods
            var propagationContext = activity.CreatePropagationContext();
            model.HydrateWithPropagationContext(m => m.TraceContext, propagationContext); 
            
            // Bonus points: There is currently discussion going on about a MQTT W3C Trace Context spec https://w3c.github.io/trace-context-mqtt/

            // prepare the payload
            var payload = JsonSerializer.Serialize(model);
            var body = Encoding.UTF8.GetBytes(payload);

            // send the message
            channel.BasicPublish(exchange: "",
                routingKey: QueueName,
                basicProperties: null,
                body: body);

            Console.WriteLine(" [x] Sent {0}", message);
        }
    }
}
