using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WeatherApi.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WeatherRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                City = table.Column<string>(type: "text", nullable: false),
                TemperatureC = table.Column<int>(type: "integer", nullable: false),
                Summary = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WeatherRecords", x => x.Id);
            });

        migrationBuilder.InsertData(
            table: "WeatherRecords",
            columns: new[] { "Id", "City", "TemperatureC", "Summary" },
            values: new object[,]
            {
                { 1, "London", 12, "Cloudy" },
                { 2, "Amsterdam", 15, "Partly sunny" },
                { 3, "Lisbon", 24, "Warm" },
                { 4, "Berlin", 9, "Cold" },
                { 5, "Paris", 17, "Mild" }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "WeatherRecords");
    }
}
