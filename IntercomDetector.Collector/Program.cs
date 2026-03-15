using System.Collections.Concurrent;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// -- SHARED STATE --
// Pending events are shared between requests since chunks of the same event
// can arrive in parallel before the final chunk closes the event.
var pendingEvents = new ConcurrentDictionary<string, ConcurrentBag<string>>();

await RawEndpoint.InitAsync();

// -- ENDPOINTS --
BufferEndpoint.Register(app, pendingEvents);
RawEndpoint.Register(app);

app.Run("http://0.0.0.0:5000");
