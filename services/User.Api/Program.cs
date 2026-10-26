var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("This is a Development-only mock API. Deploy a service that validates JWT and business authorization in Production.");
}
var app = builder.Build();

app.MapGet("/api/users", () => TypedResults.Ok(new[]
{
    new { Id = 1, Name = "User A" },
    new { Id = 2, Name = "User B" }
}));
app.MapGet("/health", () => TypedResults.Ok(new { Status = "Healthy" }));

app.Run();
