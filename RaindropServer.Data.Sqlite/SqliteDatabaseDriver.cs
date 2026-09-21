using System.Data.Common;
using Microsoft.Data.Sqlite;
using RaindropServer.Data.Relational;

namespace RaindropServer.Data.Sqlite;

/// <summary>
/// The SQLite database module — enabled with <c>"provider": "sqlite"</c>. The whole server lives in
/// a single file, which makes it the natural choice for a small or self-hosted Raindrop server that
/// does not want a database to administer.
/// </summary>
/// <remarks>
/// Example connection string: <c>Data Source=raindrop.db</c>.
/// </remarks>
public sealed class SqliteDatabaseDriver : RelationalDatabaseDriver
{
    public override string Name => "sqlite";

    protected override DbConnection CreateConnection(string connectionString) =>
        new SqliteConnection(connectionString);

    protected override IReadOnlyList<string> SchemaStatements =>
    [
        // Write-ahead logging lets readers carry on during a write, which matters once several
        // clients are talking to the server at once. The setting sticks with the database file.
        "PRAGMA journal_mode = WAL",

        """
        CREATE TABLE IF NOT EXISTS raindrop_users (
            user_id             TEXT    NOT NULL PRIMARY KEY,
            username            TEXT    NULL,
            username_normalized TEXT    NULL,
            password_hash       TEXT    NULL,
            status              INTEGER NOT NULL,
            created_at          TEXT    NOT NULL,
            last_login_at       TEXT    NULL,
            left_at             TEXT    NULL,
            data_expires_at     TEXT    NULL
        )
        """,

        // Partial index: erased accounts keep their UUID row but give up their name, and any number
        // of those may sit here with a NULL username.
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_raindrop_users_username
            ON raindrop_users (username_normalized)
            WHERE username_normalized IS NOT NULL
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_channels (
            channel_id      TEXT    NOT NULL PRIMARY KEY,
            channel_type    INTEGER NOT NULL,
            name            TEXT    NOT NULL,
            name_normalized TEXT    NOT NULL,
            created_by      TEXT    NOT NULL,
            created_at      TEXT    NOT NULL
        )
        """,

        // Channel names are unique per server, so "#general" means one place to everybody here.
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_raindrop_channels_name
            ON raindrop_channels (name_normalized)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_roles (
            role_id         TEXT    NOT NULL PRIMARY KEY,
            name            TEXT    NOT NULL,
            name_normalized TEXT    NOT NULL,
            permissions     INTEGER NOT NULL,
            sort_order      INTEGER NOT NULL,
            kind            INTEGER NOT NULL,
            created_at      TEXT    NOT NULL
        )
        """,

        // Role names are unique per server, so "give them Moderator" means one thing to everybody.
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_raindrop_roles_name
            ON raindrop_roles (name_normalized)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_member_roles (
            user_id     TEXT NOT NULL,
            role_id     TEXT NOT NULL,
            assigned_at TEXT NOT NULL,
            assigned_by TEXT NULL,
            PRIMARY KEY (user_id, role_id)
        )
        """,

        // The primary key already answers "what does this member hold"; this is the other direction,
        // which every permission change needs in order to find who was affected.
        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_member_roles_role
            ON raindrop_member_roles (role_id)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_text_messages (
            message_id TEXT NOT NULL PRIMARY KEY,
            channel_id TEXT NOT NULL,
            author_id  TEXT NOT NULL,
            content    TEXT NULL,
            sent_at    TEXT NOT NULL,
            deleted_at TEXT NULL,
            deleted_by TEXT NULL
        )
        """,

        // The one query that matters: a channel's history, newest first. The columns are in the order
        // the paging cursor compares them, so a page is a range scan rather than a sort.
        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_text_messages_history
            ON raindrop_text_messages (channel_id, sent_at, message_id)
        """,

        // The original text of deleted messages, kept for the server owner. No foreign keys, like
        // everything else here: the channel it names may well have been deleted.
        """
        CREATE TABLE IF NOT EXISTS raindrop_deleted_messages (
            message_id   TEXT NOT NULL PRIMARY KEY,
            channel_id   TEXT NOT NULL,
            channel_name TEXT NOT NULL,
            author_id    TEXT NOT NULL,
            content      TEXT NOT NULL,
            sent_at      TEXT NOT NULL,
            deleted_at   TEXT NOT NULL,
            deleted_by   TEXT NOT NULL,
            reason       TEXT NOT NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_deleted_messages_channel
            ON raindrop_deleted_messages (channel_id, deleted_at)
        """,

        // Which one-time permission rollouts this database has had — see PermissionRollouts. A row
        // per rollout, so a permission added by a later build reaches the everyone role exactly once.
        """
        CREATE TABLE IF NOT EXISTS raindrop_permission_rollouts (
            rollout_id TEXT NOT NULL PRIMARY KEY,
            applied_at TEXT NOT NULL
        )
        """,

        // Members muted in every voice call by a moderator ("mute for everyone"). One row per muted
        // member; lifting the mute deletes it.
        """
        CREATE TABLE IF NOT EXISTS raindrop_voice_mutes (
            user_id  TEXT NOT NULL PRIMARY KEY,
            muted_by TEXT NOT NULL,
            muted_at TEXT NOT NULL
        )
        """
    ];
}
