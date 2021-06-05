using System;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

[assembly: FunctionsStartup(typeof(AzureFunctionsDistributedTracingSample.AzureFunctions.Startup))]
namespace AzureFunctionsDistributedTracingSample.AzureFunctions
{

    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddHttpClient();

            // Registering Serilog provider
            var logger = new LoggerConfiguration()
                .WriteTo.Console()
                .CreateLogger();

            builder.Services.AddLogging(lb => lb.AddSerilog(logger));

            // Example from https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/examples/Console/TestConsoleExporter.cs
            var zipkinUri = "http://localhost:9411/api/v2/spans";

            var openTelemetry = Sdk.CreateTracerProviderBuilder()
                .AddHttpClientInstrumentation()
                .AddSource(Functions.ActivitySourceName)
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("zipkin-test"))
                .AddZipkinExporter(o =>
                {
                    o.Endpoint = new Uri(zipkinUri);
                })
                .AddConsoleExporter()
                .Build();

            builder.Services.AddSingleton(openTelemetry);
        }
    }
}

