using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Dochub.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dochub");

            migrationBuilder.CreateSequence(
                name: "source_document_number",
                schema: "dochub",
                startValue: 1000L);

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    initials = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queue_messages",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    queue = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    delivery_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queue_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    identity_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    can_create_organizations = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_connections",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    protected_access_token = table.Column<string>(type: "text", nullable: true),
                    protected_refresh_token = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_connections_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "dochub",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                    table.ForeignKey(
                        name: "FK_teams_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "dochub",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    can_create_organizations = table.Column<bool>(type: "boolean", nullable: false),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "dochub",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_organization_members_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "dochub",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groups", x => x.id);
                    table.ForeignKey(
                        name: "FK_groups_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "dochub",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_members",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_team_members_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "dochub",
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_team_members_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "dochub",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    primary_source = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_artifacts_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "dochub",
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recurring_sync_schedules",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    source_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_reference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_options = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    frequency = table.Column<int>(type: "integer", nullable: false),
                    time_of_day = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: true),
                    day_of_month = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_source_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_running = table.Column<bool>(type: "boolean", nullable: false),
                    run_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    documents_added_last_run = table.Column<int>(type: "integer", nullable: false),
                    documents_updated_last_run = table.Column<int>(type: "integer", nullable: false),
                    documents_unchanged_last_run = table.Column<int>(type: "integer", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recurring_sync_schedules", x => x.id);
                    table.ForeignKey(
                        name: "FK_recurring_sync_schedules_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalSchema: "dochub",
                        principalTable: "artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recurring_sync_schedules_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "dochub",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_documents",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    source_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_reference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_options = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    total_documents = table.Column<int>(type: "integer", nullable: false),
                    uploaded_document_count = table.Column<int>(type: "integer", nullable: false),
                    processed_document_count = table.Column<int>(type: "integer", nullable: false),
                    failed_document_count = table.Column<int>(type: "integer", nullable: false),
                    added_document_count = table.Column<int>(type: "integer", nullable: false),
                    updated_document_count = table.Column<int>(type: "integer", nullable: false),
                    unchanged_document_count = table.Column<int>(type: "integer", nullable: false),
                    blob_container = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    blob_prefix = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    upload_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    delivery_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_documents_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalSchema: "dochub",
                        principalTable: "artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_source_documents_users_requested_by_user_id",
                        column: x => x.requested_by_user_id,
                        principalSchema: "dochub",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "uploaded_documents",
                schema: "dochub",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_location = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    blob_path = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: true),
                    blob_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    blob_uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uploaded_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_uploaded_documents_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalSchema: "dochub",
                        principalTable: "artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_uploaded_documents_source_documents_source_document_id",
                        column: x => x.source_document_id,
                        principalSchema: "dochub",
                        principalTable: "source_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_group_id_name",
                schema: "dochub",
                table: "artifacts",
                columns: new[] { "group_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_groups_team_id_name",
                schema: "dochub",
                table: "groups",
                columns: new[] { "team_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_organization_id_user_id_created_at",
                schema: "dochub",
                table: "notifications",
                columns: new[] { "organization_id", "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_organization_id_user_id",
                schema: "dochub",
                table: "organization_members",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_user_id",
                schema: "dochub",
                table: "organization_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_slug",
                schema: "dochub",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_queue_messages_queue_message_id",
                schema: "dochub",
                table: "queue_messages",
                columns: new[] { "queue", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_queue_messages_queue_status_id",
                schema: "dochub",
                table: "queue_messages",
                columns: new[] { "queue", "status", "id" },
                filter: "status IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_sync_schedules_artifact_id_source_type",
                schema: "dochub",
                table: "recurring_sync_schedules",
                columns: new[] { "artifact_id", "source_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recurring_sync_schedules_created_by_user_id",
                schema: "dochub",
                table: "recurring_sync_schedules",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_recurring_sync_schedules_status_next_run_at",
                schema: "dochub",
                table: "recurring_sync_schedules",
                columns: new[] { "status", "next_run_at" },
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "IX_source_connections_organization_id_user_id_source_type",
                schema: "dochub",
                table: "source_connections",
                columns: new[] { "organization_id", "user_id", "source_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_artifact_id_requested_at",
                schema: "dochub",
                table: "source_documents",
                columns: new[] { "artifact_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_organization_id_requested_at",
                schema: "dochub",
                table: "source_documents",
                columns: new[] { "organization_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_reference",
                schema: "dochub",
                table: "source_documents",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_requested_by_user_id",
                schema: "dochub",
                table: "source_documents",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_status",
                schema: "dochub",
                table: "source_documents",
                column: "status",
                filter: "status < 4");

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_sync_schedule_id_requested_at",
                schema: "dochub",
                table: "source_documents",
                columns: new[] { "sync_schedule_id", "requested_at" },
                filter: "sync_schedule_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_team_members_team_id_user_id",
                schema: "dochub",
                table: "team_members",
                columns: new[] { "team_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_team_members_user_id",
                schema: "dochub",
                table: "team_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_teams_organization_id_name",
                schema: "dochub",
                table: "teams",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_teams_organization_id_slug",
                schema: "dochub",
                table: "teams",
                columns: new[] { "organization_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_uploaded_documents_artifact_id_source_type_external_id",
                schema: "dochub",
                table: "uploaded_documents",
                columns: new[] { "artifact_id", "source_type", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_uploaded_documents_artifact_id_status",
                schema: "dochub",
                table: "uploaded_documents",
                columns: new[] { "artifact_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_uploaded_documents_source_document_id",
                schema: "dochub",
                table: "uploaded_documents",
                column: "source_document_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                schema: "dochub",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_identity_provider_external_subject",
                schema: "dochub",
                table: "users",
                columns: new[] { "identity_provider", "external_subject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "organization_members",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "queue_messages",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "recurring_sync_schedules",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "source_connections",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "team_members",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "uploaded_documents",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "source_documents",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "users",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "groups",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "dochub");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "dochub");

            migrationBuilder.DropSequence(
                name: "source_document_number",
                schema: "dochub");
        }
    }
}
