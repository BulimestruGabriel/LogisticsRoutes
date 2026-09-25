using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogisticsRoutes.BusinessLayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueDailyRouteResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Routes_Date_DriverId",
                table: "Routes",
                columns: new[] { "Date", "DriverId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_Date_VehicleId",
                table: "Routes",
                columns: new[] { "Date", "VehicleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Routes_Date_DriverId",
                table: "Routes");

            migrationBuilder.DropIndex(
                name: "IX_Routes_Date_VehicleId",
                table: "Routes");
        }
    }
}
