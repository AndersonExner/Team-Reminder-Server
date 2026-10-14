using Npgsql;

namespace TeamReminder.Server;

public static class SchemaMigration
{
    public static async Task ApplyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // Serialize migrations even when multiple application instances start together.
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(734620261014);
            CREATE TABLE IF NOT EXISTS schema_versions (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM schema_versions WHERE version = 1) THEN
                CREATE TABLE "Accounts" (
                    "Id" uuid PRIMARY KEY, "Name" varchar(100) NOT NULL,
                    "Email" varchar(254) NOT NULL, "NormalizedEmail" varchar(254) NOT NULL,
                    "PasswordHash" text NOT NULL, "CreatedAt" timestamptz NOT NULL);
                CREATE UNIQUE INDEX "IX_Accounts_NormalizedEmail" ON "Accounts" ("NormalizedEmail");
                CREATE TABLE "Sessions" (
                    "Id" uuid PRIMARY KEY, "AccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
                    "AccessHash" text NOT NULL, "RefreshHash" text NOT NULL,
                    "AccessExpiresAt" timestamptz NOT NULL, "RefreshExpiresAt" timestamptz NOT NULL);
                CREATE UNIQUE INDEX "IX_Sessions_AccessHash" ON "Sessions" ("AccessHash");
                CREATE UNIQUE INDEX "IX_Sessions_RefreshHash" ON "Sessions" ("RefreshHash");
                CREATE INDEX "IX_Sessions_AccountId" ON "Sessions" ("AccountId");
                INSERT INTO schema_versions (version) VALUES (1);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM schema_versions WHERE version = 2) THEN
                ALTER TABLE "Accounts" ADD COLUMN "AlarmRevision" bigint NOT NULL DEFAULT 0;
                CREATE TABLE "PersonalAlarms" (
                    "AccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
                    "Id" uuid NOT NULL, "Version" bigint NOT NULL, "MutationId" uuid NOT NULL,
                    "Deleted" boolean NOT NULL, "Title" text NOT NULL,
                    "ScheduledAtUtc" timestamptz NOT NULL, "CreatedAtUtc" timestamptz NOT NULL,
                    "TimeZoneId" text NOT NULL, "Recurrence" integer NOT NULL, "Days" integer NOT NULL,
                    "LinkUrl" text, "LinkLabel" text, "SoundKind" integer NOT NULL,
                    PRIMARY KEY ("AccountId", "Id"));
                CREATE UNIQUE INDEX "IX_PersonalAlarms_AccountId_Version" ON "PersonalAlarms" ("AccountId", "Version");
                INSERT INTO schema_versions (version) VALUES (2);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM schema_versions WHERE version = 3) THEN
                CREATE TABLE "Groups" (
                    "Id" uuid PRIMARY KEY, "Name" text NOT NULL, "Description" text NOT NULL,
                    "OwnerAccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                    "Revision" bigint NOT NULL, "CreatedAt" timestamptz NOT NULL, "DeletedAt" timestamptz);
                CREATE TABLE "GroupMemberships" (
                    "GroupId" uuid NOT NULL REFERENCES "Groups" ("Id") ON DELETE CASCADE,
                    "AccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
                    "Role" integer NOT NULL CHECK ("Role" BETWEEN 0 AND 2), "JoinedAt" timestamptz NOT NULL,
                    PRIMARY KEY ("GroupId", "AccountId"));
                CREATE INDEX "IX_GroupMemberships_AccountId" ON "GroupMemberships" ("AccountId");
                CREATE TABLE "GroupInvitations" (
                    "Id" uuid PRIMARY KEY, "GroupId" uuid NOT NULL REFERENCES "Groups" ("Id") ON DELETE CASCADE,
                    "RecipientAccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE CASCADE,
                    "InviterAccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                    "Status" integer NOT NULL CHECK ("Status" BETWEEN 0 AND 4),
                    "CreatedAt" timestamptz NOT NULL, "ExpiresAt" timestamptz NOT NULL);
                CREATE INDEX "IX_GroupInvitations_GroupId_RecipientAccountId_Status" ON "GroupInvitations" ("GroupId", "RecipientAccountId", "Status");
                CREATE INDEX "IX_GroupInvitations_RecipientAccountId_Status" ON "GroupInvitations" ("RecipientAccountId", "Status");
                CREATE TABLE "GroupAuditEvents" (
                    "Id" uuid PRIMARY KEY, "GroupId" uuid NOT NULL REFERENCES "Groups" ("Id") ON DELETE CASCADE,
                    "ActorAccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                    "TargetAccountId" uuid, "Action" text NOT NULL, "CreatedAt" timestamptz NOT NULL);
                CREATE INDEX "IX_GroupAuditEvents_GroupId_CreatedAt" ON "GroupAuditEvents" ("GroupId", "CreatedAt");
                INSERT INTO schema_versions (version) VALUES (3);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM schema_versions WHERE version = 4) THEN
                ALTER TABLE "Accounts" ADD COLUMN "IsActive" boolean NOT NULL DEFAULT true,
                    ADD COLUMN "IsSystemAdmin" boolean NOT NULL DEFAULT false,
                    ADD COLUMN "DeletedAt" timestamptz,
                    ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
                    ADD COLUMN "SessionVersion" bigint NOT NULL DEFAULT 0;
                ALTER TABLE "Sessions" ADD COLUMN "AccountVersion" bigint NOT NULL DEFAULT 0;
                ALTER TABLE "Groups" ADD COLUMN "IsActive" boolean NOT NULL DEFAULT true;
                CREATE TABLE "AdminAuditEvents" (
                    "Id" uuid PRIMARY KEY, "ActorAccountId" uuid NOT NULL REFERENCES "Accounts" ("Id"),
                    "TargetId" uuid NOT NULL, "Entity" text NOT NULL, "Action" text NOT NULL,
                    "CreatedAt" timestamptz NOT NULL);
                CREATE INDEX "IX_AdminAuditEvents_CreatedAt" ON "AdminAuditEvents" ("CreatedAt");
                INSERT INTO schema_versions (version) VALUES (4);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM schema_versions WHERE version = 5) THEN
                CREATE TABLE "GroupAlarms" (
                    "Id" uuid PRIMARY KEY, "GroupId" uuid NOT NULL REFERENCES "Groups" ("Id") ON DELETE RESTRICT,
                    "CreatedByAccountId" uuid NOT NULL REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
                    "Version" bigint NOT NULL, "MutationId" uuid NOT NULL, "Deleted" boolean NOT NULL,
                    "DefinitionJson" text NOT NULL);
                CREATE INDEX "IX_GroupAlarms_GroupId" ON "GroupAlarms" ("GroupId");
                INSERT INTO schema_versions(version) VALUES (5);
            END IF;
            END $$;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
