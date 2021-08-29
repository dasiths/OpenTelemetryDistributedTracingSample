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
using RabbitMQ.Client.Events;

namespace DistributedTracingSample.Messaging.Consumer
{
    class Program
    {
        const string ZipkinUri = "http://localhost:9411/api/v2/spans";
        const string ActivitySourceName = "message-consumer-test-source";
        const string QueueName = "distributed-tracing-sample-queue";
        const string ServiceName = "message-consumer";

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
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            
            // declare queue
            channel.QueueDeclare(queue: QueueName,
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            // setup the received event handler
            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += 
                (_, eventArgs) =>  ReceivedEventHandler(eventArgs);
                    
            // subscribe to the queue
            channel.BasicConsume(queue: QueueName,
                autoAck: true,
                consumer: consumer);

            Console.WriteLine("Press return to exit...");
            Console.ReadLine();
        }

        private static void ReceivedEventHandler(BasicDeliverEventArgs eventArgs)
        {
            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };

            var body = eventArgs.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            Console.WriteLine(" [x] Received {0}", message);

            // convert payload to model
            var model = JsonSerializer.Deserialize<MessageWrapper<string>>(message);

            // create the trace context from the model
            var propagationContext = model.ExtractPropagationContext(m => m.TraceProperties);

            // create the activity source and activity
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("Function-Consumer", ActivityKind.Consumer, propagationContext);

            // now we are inside the context of the child activity
            Console.WriteLine($"TraceId: {activity?.Context.TraceId}");
            Console.WriteLine($"Baggage: {JsonSerializer.Serialize(Baggage.Current.GetBaggage(), serializerSettings)}");
        }
    }
}
