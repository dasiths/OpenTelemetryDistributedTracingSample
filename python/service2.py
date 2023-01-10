import uuid
import paho.mqtt.client as mqtt
from paho.mqtt.properties import Properties
from paho.mqtt.packettypes import PacketTypes
import requests

from opentelemetry import trace
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator
from opentelemetry.trace import Status, StatusCode, SpanKind
from opentelemetry.sdk.resources import SERVICE_NAME, SERVICE_INSTANCE_ID, Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import (
    BatchSpanProcessor,
    ConsoleSpanExporter,
)

from opentelemetry.instrumentation.requests import RequestsInstrumentor
from opentelemetry.exporter.zipkin.json import ZipkinExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter

from opentelemetry import metrics
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader, ConsoleMetricExporter

from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter

otlp_endpoint = "http://127.0.0.1:4317"


def add_console_exporter(provider: TracerProvider):
    processor = BatchSpanProcessor(span_exporter=ConsoleSpanExporter(), schedule_delay_millis=1000)
    provider.add_span_processor(processor)


def add_zipkin_exporter(provider: TracerProvider):
    zipkin_exporter = ZipkinExporter(
        # version=Protocol.V2
        # optional:
        endpoint="http://localhost:9411/api/v2/spans",
        # local_node_ipv4="192.168.0.1",
        # local_node_ipv6="2001:db8::c001",
        # local_node_port=31313,
        # max_tag_value_length=256,
        # timeout=5 (in seconds),
        # session=requests.Session(),
    )
    zipkin_span_processor = BatchSpanProcessor(span_exporter=zipkin_exporter, schedule_delay_millis=1000)
    provider.add_span_processor(zipkin_span_processor)


def add_otlp_exporter(provider: TracerProvider):
    otlp_exporter = OTLPSpanExporter(endpoint=otlp_endpoint, insecure=True)
    otlp_span_processor = BatchSpanProcessor(span_exporter=otlp_exporter, schedule_delay_millis=1000)
    provider.add_span_processor(otlp_span_processor)


resource = Resource.create({SERVICE_NAME: "Service2", SERVICE_INSTANCE_ID: "1"})
provider = TracerProvider(
            # This can also be read from envrionment variables https://opentelemetry.io/docs/reference/specification/sdk-environment-variables/
            resource=resource
           )

# setup the exporters
add_console_exporter(provider)
# add_zipkin_exporter(provider)
add_otlp_exporter(provider)

# Sets the global default tracer provider
trace.set_tracer_provider(provider)

# Instrument requests
# https://github.com/open-telemetry/opentelemetry-python-contrib/blob/main/instrumentation/opentelemetry-instrumentation-requests/src/opentelemetry/instrumentation/requests/__init__.py
RequestsInstrumentor().instrument()

# Creates a tracer from the global tracer provider
tracer = trace.get_tracer("Service2")

console_metric_reader = PeriodicExportingMetricReader(exporter=ConsoleMetricExporter(), export_interval_millis=1000)
otlp_metric_reader = PeriodicExportingMetricReader(exporter=OTLPMetricExporter(endpoint=otlp_endpoint, insecure=True),
                                                   export_interval_millis=1000)
meter_provider = MeterProvider(resource=resource,
                               metric_readers=[console_metric_reader, otlp_metric_reader])
metrics.set_meter_provider(meter_provider=meter_provider)

# Create meter from global meter provider
meter = metrics.get_meter("Service2", "1.0")

# Based on example from https://stackoverflow.com/questions/68530363/opentelemetry-python-how-to-instanciate-a-new-span-as-a-child-span-for-a-given

broker_address = "your-mqtt-broker.local"
client = mqtt.Client(client_id="service2" + str(uuid.uuid4()), transport="tcp", protocol=mqtt.MQTTv5)
client.username_pw_set("username", "password")

# Example metric https://github.com/open-telemetry/opentelemetry-python/tree/main/exporter/opentelemetry-exporter-prometheus

counter = meter.create_counter("message_count", "messages", "number of messages")
labels = {"environment": "staging"}


def on_connect(client, userdata, flags, rc, properties=None):
    while (rc != 0):
        print("connecting to MQTT Broker!")

    print("Connected to MQTT broker %d\n", rc)
    client.subscribe("otel-demo/output1")


def on_message(client, userdata, msg):
    payload = msg.payload.decode("utf-8")
    print(f"MQTT msg recieved: {payload}")
    counter.add(1, labels)

    # We need to extract the propagation context from user properties https://w3c.github.io/trace-context-mqtt/#trace-context-fields-placement-in-a-message
    prop = TraceContextTextMapPropagator()
    user_properties = dict(msg.properties.UserProperty)
    print("Carrier with span context", user_properties)
    ctx = prop.extract(carrier=user_properties)

    # Create a new span with context extracted from message
    with tracer.start_as_current_span("Service2_Receive_Message", context=ctx, kind=SpanKind.SERVER):
        current_span = trace.get_current_span()
        current_span.add_event("Gonna try to send message!")  # Events are are primitive logs
        publish_message(payload)
        current_span.add_event("Published message!")
        make_http_call(payload)
        current_span.add_event("Made http call!")
        pass


@tracer.start_as_current_span("Service2_Publish_Message", kind=SpanKind.PRODUCER)
def publish_message(payload):
    # We are injecting the current propagation context into the mqtt message as per https://w3c.github.io/trace-context-mqtt/#mqtt-v5-0-format
    carrier = {}
    propagator = TraceContextTextMapPropagator()
    propagator.inject(carrier=carrier)

    properties = Properties(PacketTypes.PUBLISH)
    properties.UserProperty = list(carrier.items())
    print("Carrier after injecting span context", properties.UserProperty)

    # publish
    client.publish("otel-demo/output2", payload, properties=properties, retain=True)


@tracer.start_as_current_span("Service2_Http_Call", kind=SpanKind.CLIENT)
def make_http_call(msg):
    try:
        # make a call to the .NET web api
        x = requests.get(f'http://localhost:5000/api/values/sayhello?name={msg}')
        print(x.text)
    except Exception as ex:
        current_span = trace.get_current_span()
        current_span.set_status(Status(StatusCode.ERROR))
        current_span.record_exception(ex)


def main(args=None):
    try:
        client.on_message = on_message
        client.on_connect = on_connect
        client.connect(broker_address, port=1883, keepalive=60)

        client.loop_forever()
    except KeyboardInterrupt:
        # todo
        print("Exiting...")
        client.disconnect()
    except Exception as ex:
        current_span = trace.get_current_span()
        current_span.set_status(Status(StatusCode.ERROR))
        current_span.record_exception(ex)
        client.disconnect()
        raise ex


if __name__ == "__main__":
    main()
