using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClassroomController.Server.Migrations
{
    /// <inheritdoc />
    public partial class StatefulDevicePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockedWebsites",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsAdminMode",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsFrozen",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TimerEndTime",
                table: "Devices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedWebsites",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IsAdminMode",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IsFrozen",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "TimerEndTime",
                table: "Devices");
        }
    }
}
