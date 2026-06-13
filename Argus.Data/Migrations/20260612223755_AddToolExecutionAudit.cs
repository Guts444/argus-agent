using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Argus.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddToolExecutionAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolExecutionAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    RiskLevel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ArgumentsSummary = table.Column<string>(type: "TEXT", nullable: false),
                    ResultSummary = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolExecutionAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutionAudits_AgentRunId",
                table: "ToolExecutionAudits",
                column: "AgentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutionAudits_ConversationId",
                table: "ToolExecutionAudits",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutionAudits_ExecutionId",
                table: "ToolExecutionAudits",
                column: "ExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutionAudits_StartedAt",
                table: "ToolExecutionAudits",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolExecutionAudits");
        }
    }
}
