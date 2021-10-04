using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace DistributedTracingSample.WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ValuesController : ControllerBase
    {
        [HttpGet("SayHello")]
        public async Task<ActionResult<string>> SayHello(string name, [FromServices] ILogger<ValuesController> logger)
        {
            // get the current span and baggage
            var activity = Activity.Current;
            var baggage = Baggage.Current;

            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };


            logger.LogInformation($"TraceId: {activity?.Context.TraceId} \n" +
                                  $"Baggage: {JsonSerializer.Serialize(baggage.GetBaggage(), serializerSettings)}");

            // print the headers of the incoming request
            var requestHeaders = this.Request.Headers;
            logger.LogInformation($"\n-----------\n" +
                                  $"Headers are:\n" +
                                  $"{string.Join("\n", requestHeaders.Select(kvp => $"Key: {kvp.Key}, Value: {kvp.Value}"))}\n" +
                                  $"-----------\n");

            // add a tag to this span
            activity?.AddTag("name", name);

            // add an event to this span
            activity?.AddEvent(new ActivityEvent("Calling sub activities"));

            // call sub activities
            await CallBackendApi();
            await SaveToDatabase();

            return $"Hello {name}";
        }

        private async Task SaveToDatabase()
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

        private async Task CallBackendApi()
        {
            // This method is a placeholder for triggering some kind to backend activity hosted on another service

            const string backendServiceName = "my-backend-service";
            
            // this tag specifies the remote service name https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/span-general.md#general-remote-service-attributes
            const string remoteServiceTagName = "peer.service";

            // create new span via .NET Activity API.
            // We don't have to specify the context as this method was called from another method in the same process.
            // It will automatically be linked to existing span as a parent because .NET implicitly passes the context information.
            using var source = new ActivitySource(Startup.ActivitySourceName);
            using var activity = source.StartActivity("Call backend service", ActivityKind.Client);
            
            // set the remote service name
            activity?.AddTag(remoteServiceTagName, backendServiceName);

            // add an event to this span
            activity?.AddEvent(new ActivityEvent("This is an example event"));
            
            // Add delay here to simulate a backend call
            await Task.Delay(200); 
        }
    }
}
