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
            var activity = Activity.Current;
            var baggage = Baggage.Current;

            var serializerSettings = new JsonSerializerOptions()
            {
                WriteIndented = true
            };

            if (activity != null)
            {
                logger.LogInformation($"TraceId: {activity.Context.TraceId} \n" +
                                      $"Baggage: {JsonSerializer.Serialize(baggage.GetBaggage(), serializerSettings)}");
            }

            var requestHeaders = this.Request.Headers;
            logger.LogInformation($"\n-----------\n" +
                                  $"Headers are:\n" +
                                  $"{string.Join("\n", requestHeaders.Select(kvp => $"Key: {kvp.Key}, Value: {kvp.Value}"))}\n" +
                                  $"-----------\n");

            await SaveToDatabase();
            return $"Hello {name}";
        }

        private async Task SaveToDatabase()
        {
            using var source = new ActivitySource(Startup.ActivitySourceName);
            using var activity = source.StartActivity("save to the database", ActivityKind.Internal);

            activity?.AddEvent(new ActivityEvent("About to start writing to the database"));
            await Task.Delay(200);
        }
    }
}
