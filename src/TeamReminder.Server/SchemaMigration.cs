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
            END $$;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
