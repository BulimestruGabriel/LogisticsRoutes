using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LogisticsRoutes.BusinessLayer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCopiedOrderProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceOrderId",
                table: "Orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SourceOrderId_DeliveryDate",
                table: "Orders",
                columns: new[] { "SourceOrderId", "DeliveryDate" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Orders_SourceOrderId",
                table: "Orders",
                column: "SourceOrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Orders_SourceOrderId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SourceOrderId_DeliveryDate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SourceOrderId",
                table: "Orders");
        }
    }
}
