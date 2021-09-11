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
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            // setup the trace provider
            using var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                .AddSource(ActivitySourceName)
                .AddZipkinExporter(o =>
                {
                    o.Endpoint = new Uri(ZipkinUri);
                })
                .AddConsoleExporter()
                .Build();

            // create RabbitMQ connection
            var factory = new ConnectionFactory() { HostName = "localhost" };
            using (var connection = factory.CreateConnection())
            {
                using (var channel = connection.CreateModel())
                {
                    channel.QueueDeclare(queue: QueueName,
                        durable: false,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);
                    
                    Console.WriteLine("Press return to send the message...");
                    Console.ReadLine();

                    // send the message
                    SendMessage(channel);
                }
            }

            Console.WriteLine("Press return to exit...");
            Console.ReadLine();
        }

        private static void SendMessage(IModel channel)
        {
            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };

            // create the activity source and activity
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("outgoing message", ActivityKind.Producer);

            // some sample baggage
            Baggage.Current = Baggage.SetBaggage(new KeyValuePair<string, string>[]
            {
                new("key1", "value1"),
                new("key2", "value2"),
                new("key3", "value3"),
            });

            Console.WriteLine($"TraceId: {activity?.Context.TraceId}");
            Console.WriteLine($"Baggage: {JsonSerializer.Serialize(Baggage.Current.GetBaggage(), serializerSettings)}");

            // some sample tags
            activity?.SetTag("environment.machineName", Environment.MachineName);
            activity?.SetTag("environment.osVersion", Environment.OSVersion);

            // create the model
            var message = "Hello World!";
            var model = new MessageWrapper<string>(message);

            // create the propagation context and hydrate the model with it
            var propagationContext = activity.CreatePropagationContext();
            model.HydrateWithPropagationContext(m => m.TraceProperties, propagationContext);

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
