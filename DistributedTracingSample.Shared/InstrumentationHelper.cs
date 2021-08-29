using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace DistributedTracingSample.Shared
{
    public static class InstrumentationHelper
    {
        // Using propagation context https://www.mytechramblings.com/posts/getting-started-with-opentelemetry-and-dotnet-core/
        // Instrumentation sample https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/src/OpenTelemetry.Instrumentation.Http/Implementation
        
        // Activity.Baggage vs Baggage API https://github.com/open-telemetry/opentelemetry-dotnet/issues/1842 

        // Text Map Propagator Interface https://github.com/open-telemetry/opentelemetry-dotnet/blob/5ddf9a486e755c53ab73debf87286a934fcbbb51/src/OpenTelemetry.Api/Context/Propagation/TextMapPropagator.cs
        // Trace Context Propagator https://github.com/open-telemetry/opentelemetry-dotnet/blob/5ddf9a486e755c53ab73debf87286a934fcbbb51/src/OpenTelemetry.Api/Context/Propagation/TraceContextPropagator.c
        // Baggage Propagator https://github.com/open-telemetry/opentelemetry-dotnet/blob/5ddf9a486e755c53ab73debf87286a934fcbbb51/src/OpenTelemetry.Api/Context/Propagation/BaggagePropagator.cs
        // Composite Text Map Propagator https://github.com/open-telemetry/opentelemetry-dotnet/blob/5ddf9a486e755c53ab73debf87286a934fcbbb51/src/OpenTelemetry.Api/Context/Propagation/CompositeTextMapPropagator.cs

        // DefaultTextMapPropagator = Composite (Trace + Baggage) https://github.com/open-telemetry/opentelemetry-dotnet/blob/6b7f2dd77cf9d37260a853fcc95f7b77e296065d/src/OpenTelemetry/Sdk.cs

        private static readonly TextMapPropagator TextMapPropagator = Propagators.DefaultTextMapPropagator;

        // Create propagation context from Activity and set baggage
        public static PropagationContext CreatePropagationContext(this Activity activity, bool includeCurrentOpenTelemetryBaggage = true, bool includeCurrentActivityBaggage = false)
        {
            var currentBaggage = new Baggage()
                .SetBaggage(includeCurrentOpenTelemetryBaggage ? Baggage.Current.GetBaggage() : new Dictionary<string, string>())
                .SetBaggage(includeCurrentActivityBaggage ? activity.Baggage : new Dictionary<string, string>());

            return new PropagationContext(activity.Context, currentBaggage); // create new propagation context with baggage
        }

        // Create propagation context from parent context and set baggage
        public static PropagationContext CreateChildPropagationContext(this PropagationContext parentContext, bool includeParentBaggage = true, bool includeCurrentOpenTelemetryBaggage = true, bool includeCurrentActivityBaggage = false)
        {
            var currentBaggageItems = Baggage.Current.GetBaggage(); // We need to do this before creating a new baggage which affect current
            var currentBaggage = new Baggage()
                    .SetBaggage(includeParentBaggage ? parentContext.Baggage.GetBaggage() : new Dictionary<string, string>())
                    .SetBaggage(includeCurrentOpenTelemetryBaggage ? currentBaggageItems : new Dictionary<string, string>());

            if (Activity.Current != null && includeCurrentActivityBaggage)
            {
                currentBaggage = currentBaggage.SetBaggage(Activity.Current.Baggage);
            }

            return new PropagationContext(parentContext.ActivityContext, currentBaggage); // create new propagation context with baggage
        }

        // Use to hydrate a dictionary with current propagation context
        public static void HydrateWithPropagationContext<T>(this T carrier, Func<T, Dictionary<string, string>> getTracePropertiesFunc, PropagationContext context)
        {
            getTracePropertiesFunc(carrier).HydrateWithPropagationContext(context);
        }

        // Use to hydrate a dictionary with current propagation context
        public static void HydrateWithPropagationContext(this Dictionary<string, string> traceProperties, PropagationContext context)
        {
            TextMapPropagator.Inject(context, traceProperties,
                (properties, key, value) =>
                {
                    properties[key] = value;
                });
        }

        // Extract the propagation context from the dictionary
        public static PropagationContext ExtractPropagationContext<T>(this T carrier, Func<T, Dictionary<string, string>> propertiesFunc)
        {
            return propertiesFunc(carrier).ExtractPropagationContext();
        }

        // Extract the propagation context from the dictionary
        public static PropagationContext ExtractPropagationContext(this Dictionary<string, string> properties)
        {
            var propagationContext = TextMapPropagator.Extract(default, properties, (props, key) =>
            {
                if (props.TryGetValue(key, out var value))
                {
                    return new[] { value };
                }

                return Enumerable.Empty<string>();
            });

            return propagationContext;
        }

        public static Activity StartActivity(this ActivitySource source, string activityName, ActivityKind kind)
        {
            return source.StartActivity(activityName, kind);
        }

        public static Activity StartActivity(this ActivitySource source, string activityName, ActivityKind kind, PropagationContext propagationContext, bool setCurrentOpenTelemetryBaggage = true, bool setCurrentActivityBaggage = false)
        {
            var activity = source.StartActivity(activityName, kind, propagationContext.ActivityContext);

            if (setCurrentOpenTelemetryBaggage)
            {
                Baggage.Current.SetBaggage(propagationContext.Baggage.GetBaggage());
            }
            
            if (setCurrentActivityBaggage)
            {
                foreach (var (key, value) in propagationContext.Baggage)
                {
                    activity?.AddBaggage(key, value);
                }
            }

            return activity;
        }
    }
}
