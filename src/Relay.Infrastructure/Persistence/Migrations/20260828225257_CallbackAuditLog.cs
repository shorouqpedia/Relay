using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CallbackAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "callback_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    disposition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    receipt_count = table.Column<int>(type: "integer", nullable: false),
                    applied_count = table.Column<int>(type: "integer", nullable: false),
                    body_preview = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_callback_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_callback_records_provider_received_at",
                table: "callback_records",
                columns: new[] { "provider_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_callback_records_rejected",
                table: "callback_records",
                column: "received_at",
                filter: "disposition IN ('Rejected', 'Malformed', 'UnknownProvider')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "callback_records");
        }
    }
}
