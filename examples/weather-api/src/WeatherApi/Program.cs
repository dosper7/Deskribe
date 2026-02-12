using Microsoft.EntityFrameworkCore;
using WeatherApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<WeatherDb>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("WeatherDb")));

var app = builder.Build();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WeatherDb>();
    db.Database.Migrate();
}

app.MapGet("/weatherforecast", async (WeatherDb db) =>
{
    return await db.WeatherRecords.ToListAsync();
});

app.MapGet("/health", async (WeatherDb db) =>
{
    try
    {
        await db.Database.CanConnectAsync();
        return Results.Ok(new { status = "healthy", database = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new { status = "unhealthy", database = ex.Message },
            statusCode: 503);
    }
});

app.Run();
