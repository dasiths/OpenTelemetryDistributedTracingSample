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
            activity?.AddEvent(new ActivityEvent("Calling SaveToDatabase()"));

            // call sub activity
            await SaveToDatabase();
            return $"Hello {name}";
        }

        private async Task SaveToDatabase()
        {
            using var source = new ActivitySource(Startup.ActivitySourceName);

            // create new span. It will automatically be linked to existing span as a parent.
            using var activity = source.StartActivity("save to the database", ActivityKind.Internal);

            // add an event to this span
            activity?.AddEvent(new ActivityEvent("About to start writing to the database"));
            await Task.Delay(200);
        }
    }
}
