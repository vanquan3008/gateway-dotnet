var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("This is a Development-only mock API. Deploy a service that validates JWT and business authorization in Production.");
}
var app = builder.Build();

app.MapGet("/api/products", () => TypedResults.Ok(new[]
{
    new { Id = 1, Name = "Product A" }
}));
app.MapGet("/health", () => TypedResults.Ok(new { Status = "Healthy" }));

app.Run();
