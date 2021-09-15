using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
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
        private const string ZipkinUri = "http://localhost:9411/api/v2/spans";
        private const string ActivitySourceName = "message-consumer-test-source";
        private const string QueueName = "distributed-tracing-sample-queue";
        private const string ServiceName = "message-consumer";

        static void Main(string[] args)
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            // setup the trace provider
            using var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                .AddSource(ActivitySourceName) // Opting in to any spans coming from this source
                .AddZipkinExporter(o =>
                {
                    o.Endpoint = new Uri(ZipkinUri); // Asking OpenTelemetry collector to export traces to Zipkin
                })
                .AddConsoleExporter() // also export to console
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

            var body = eventArgs.Body.ToArray(); // this is the raw message
            var message = Encoding.UTF8.GetString(body); // convert it to string
            Console.WriteLine(" [x] Received {0}", message);

            // convert the string payload back to the message envelope type
            var model = JsonSerializer.Deserialize<MyMessageEnvelope<string>>(message);

            // create the trace context from the model
            var propagationContext = model.ExtractPropagationContext(m => m.TraceContext);

            // create the span via .NET Activity API
            // this time we pass the propagation context which we extracted from the message payload.
            // so the new span knows the context info like trace id and parent span id.
            // StartActivity() here comes from a helper class.
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("Message-Processing", ActivityKind.Consumer, propagationContext);

            // now we are inside the context of the child activity
            Console.WriteLine($"TraceId: {activity?.Context.TraceId}");
            Console.WriteLine($"Baggage: {JsonSerializer.Serialize(Baggage.Current.GetBaggage(), serializerSettings)}");

            // call sub activity
            SaveToDatabase();
        }

        private static void SaveToDatabase()
        {
            // create new span via .NET Activity API.
            // We don't have to specify the context as this method was called from another method in the same process.
            // It will automatically be linked to existing span as a parent because .NET implicitly passes the context information.
            using var source = new ActivitySource(ActivitySourceName);
            using var activity = source.StartActivity("save to the database", ActivityKind.Internal);

            // using events to log information to the trace
            activity?.AddEvent(new ActivityEvent("About to start writing to the database"));
            
            // using tags to enrich the span information
            activity?.AddTag("some header", "some value");
            
            Task.Delay(200).ConfigureAwait(false).GetAwaiter().GetResult();
        }
    }
}
