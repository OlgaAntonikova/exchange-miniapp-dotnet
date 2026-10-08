var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

await app.RunAsync().ConfigureAwait(false);

/// <summary>The entry point; public so that integration tests can host the app.</summary>
public partial class Program;
