using System;
using System.Collections.Generic;
using System.Data.SqlClient;
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
            // Use the W3C trace context https://www.w3.org/TR/trace-context/
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            // setup the trace provider
            using var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                .AddSource(ActivitySourceName) // Opting in to any spans coming from this source
                .AddHttpClientInstrumentation() // Opting in for http client instrumentation from OpenTelemetry.Instrumentation.SqlClient nuget library
                .AddSqlClientInstrumentation() // Opting in for sql client instrumentation from OpenTelemetry.Instrumentation.Http nuget library
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

            // call sub activities
            CallExternalService().ConfigureAwait(false).GetAwaiter().GetResult();
            SaveToDatabase().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static async Task SaveToDatabase()
        {
            // We create a SQL Client here and do a sample call to the database to show the instrumented library capabilities
            // SqlClient is instrumented via OpenTelemetry.Instrumentation.SqlClient library

            const string sqlConnectionString = @"Data Source=localhost;Initial Catalog=master;Integrated Security=True;"; // todo: Update this to point to your local sql instance
            const string queryString = @"SELECT CAST( GETDATE() AS Date ) ;";

            await using SqlConnection connection = new SqlConnection(sqlConnectionString);
            var command = new SqlCommand(queryString, connection);
            connection.Open();
            await command.ExecuteReaderAsync();
        }

        private static async Task CallExternalService()
        {
            // make the http calls to external services.
            // HttpClient is instrumented via OpenTelemetry.Instrumentation.Http library
            
            var client = new System.Net.Http.HttpClient();
            var result = await client.GetAsync(new Uri(@"https://www.google.com/search?q=open+telemetry+.net"));
            Console.WriteLine($"External service status code = {result.StatusCode}");

            result = await client.GetAsync(new Uri(@"https://www.bing.com/search?q=open+telemetry+.net"));
            Console.WriteLine($"External service status code = {result.StatusCode}");
        }
    }
}
