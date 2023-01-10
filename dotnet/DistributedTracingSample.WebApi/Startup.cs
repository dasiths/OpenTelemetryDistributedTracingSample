using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DistributedTracingSample.WebApi
{
    public class Startup
    {
        private const string ZipkinUri = "http://localhost:9411/api/v2/spans";
        private const string ServiceName = "http-server";
        public const string ActivitySourceName = "sample-source";

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Use the W3C trace context https://www.w3.org/TR/trace-context/
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            services.AddControllers();
            
            // Setup OpenTelemetry tracing
            services.AddOpenTelemetryTracing(
                (builder) => builder
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                    .AddSource(ActivitySourceName) // Opting in to any spans coming from this source
                    .AddAspNetCoreInstrumentation() // Opting in for aspnet core instrumentation from OpenTelemetry.Instrumentation.AspNetCore nuget library
                    .AddSqlClientInstrumentation() // Opting in for http client instrumentation from OpenTelemetry.Instrumentation.SqlClient nuget library
                    //.AddZipkinExporter(o =>
                    //{
                    //    o.Endpoint = new Uri(ZipkinUri); // Asking OpenTelemetry collector to export traces to Zipkin via OpenTelemetry.Exporter.Zipkin nuget library
                    //})
                    .AddConsoleExporter() // Also export to console via OpenTelemetry.Exporter.Console nuget library
                    .AddOtlpExporter(opt =>
                    {
                        opt.Endpoint = new Uri("http://127.0.0.1:4317");
                        opt.Protocol = OtlpExportProtocol.Grpc;
                    }) // Also export to OTLP collector via OpenTelemetry.Exporter.OpenTelemetryProtocol nuget library
                );
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            // app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
