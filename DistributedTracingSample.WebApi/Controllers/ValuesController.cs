using System.Diagnostics;
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
        public async Task<ActionResult<string>> SayHello(string name, [FromServices]ILogger<ValuesController> logger)
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
            
            return $"Hello {name}";
        }
    }
}
