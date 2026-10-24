using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaimPortal.Notifications.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "notifications",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipient = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    first_attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.message_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_claim_number",
                schema: "notifications",
                table: "notification_deliveries",
                column: "claim_number");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_status_last_attempted_at",
                schema: "notifications",
                table: "notification_deliveries",
                columns: new[] { "status", "last_attempted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "notifications");
        }
    }
}
