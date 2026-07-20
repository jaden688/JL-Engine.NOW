using JLEngine.Runtime;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var shared = await RuntimeComposition.BuildSharedAsync(Directory.GetCurrentDirectory());
app.MapChatEndpoints(shared, new SessionRegistry(shared));

app.Run();

// Exposed for integration tests via WebApplicationFactory-style access.
public partial class Program;
