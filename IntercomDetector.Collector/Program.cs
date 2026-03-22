using System.Collections.Concurrent;
using IntercomDetector.Core;
using IntercomDetector.Core.Pipeline;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// -- CAPTURES FOLDER --
var capturesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");

// -- BUILD PIPELINE --
var eventProcessor = new EventProcessor(capturesFolder);
var pipeline = new SamplePipeline(new ISampleProcessor[]
{
    new RawWriter(capturesFolder),
    eventProcessor,
    new RestWriter(capturesFolder),
});

// -- SHARED STATE (for legacy BufferEndpoint) --
var pendingEvents = new ConcurrentDictionary<string, ConcurrentBag<string>>();

// -- INIT --
await RawEndpoint.InitAsync(pipeline, eventProcessor);

// -- ENDPOINTS --
BufferEndpoint.Register(app, pendingEvents);
RawEndpoint.Register(app);

app.Run("http://0.0.0.0:5000");
