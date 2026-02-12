using Microsoft.EntityFrameworkCore;

namespace WeatherApi;

public class WeatherDb : DbContext
{
    public WeatherDb(DbContextOptions<WeatherDb> options) : base(options) { }

    public DbSet<WeatherRecord> WeatherRecords => Set<WeatherRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WeatherRecord>().HasData(
            new WeatherRecord { Id = 1, City = "London", TemperatureC = 12, Summary = "Cloudy" },
            new WeatherRecord { Id = 2, City = "Amsterdam", TemperatureC = 15, Summary = "Partly sunny" },
            new WeatherRecord { Id = 3, City = "Lisbon", TemperatureC = 24, Summary = "Warm" },
            new WeatherRecord { Id = 4, City = "Berlin", TemperatureC = 9, Summary = "Cold" },
            new WeatherRecord { Id = 5, City = "Paris", TemperatureC = 17, Summary = "Mild" }
        );
    }
}

public class WeatherRecord
{
    public int Id { get; set; }
    public required string City { get; set; }
    public int TemperatureC { get; set; }
    public string? Summary { get; set; }
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
