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
        """,

        // End-to-end encryption: users' public identity keys, their devices, and sealed key backups.
        // Only public keys and opaque blobs — nothing here decrypts anything.
        """
        CREATE TABLE IF NOT EXISTS raindrop_user_identities (
            user_id      TEXT NOT NULL PRIMARY KEY,
            identity_key BLOB NOT NULL,
            updated_at   TEXT NOT NULL
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_devices (
            user_id       TEXT NOT NULL,
            device_id     TEXT NOT NULL,
            signature_key BLOB NOT NULL,
            certificate   BLOB NOT NULL,
            created_at    TEXT NOT NULL,
            revoked_at    TEXT NULL,
            PRIMARY KEY (user_id, device_id)
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_key_backups (
            user_id    TEXT NOT NULL PRIMARY KEY,
            blob_data  BLOB NOT NULL,
            updated_at TEXT NOT NULL
        )
        """,

        // The MLS delivery service: groups (one per channel), their commit logs, devices' key
        // packages and pending Welcomes. Every message is opaque; the server cannot read any of it.
        // The commit log's key (channel_id, epoch) is what refuses a second commit for an epoch.
        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_groups (
            channel_id       TEXT    NOT NULL PRIMARY KEY,
            epoch            INTEGER NOT NULL,
            group_info       BLOB    NULL,
            group_info_epoch INTEGER NOT NULL,
            updated_at       TEXT    NOT NULL
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_commits (
            channel_id     TEXT    NOT NULL,
            epoch          INTEGER NOT NULL,
            message        BLOB    NOT NULL,
            sender_user_id TEXT    NOT NULL,
            created_at     TEXT    NOT NULL,
            PRIMARY KEY (channel_id, epoch)
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_key_packages (
            package_id  TEXT    NOT NULL PRIMARY KEY,
            user_id     TEXT    NOT NULL,
            device_id   TEXT    NOT NULL,
            key_package BLOB    NOT NULL,
            last_resort INTEGER NOT NULL,
            created_at  TEXT    NOT NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_mls_key_packages_device
            ON raindrop_mls_key_packages (user_id, device_id, last_resort, created_at)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_welcomes (
            welcome_id TEXT    NOT NULL PRIMARY KEY,
            user_id    TEXT    NOT NULL,
            device_id  TEXT    NOT NULL,
            channel_id TEXT    NOT NULL,
            epoch      INTEGER NOT NULL,
            welcome    BLOB    NOT NULL,
            created_at TEXT    NOT NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_mls_welcomes_device
            ON raindrop_mls_welcomes (user_id, device_id)
        """,

        // End-to-end encrypted messages: MLS ciphertext the server cannot read. A table of its own so
        // existing databases gain it without an ALTER; history reads merge it with the one above.
        """
        CREATE TABLE IF NOT EXISTS raindrop_encrypted_messages (
            message_id TEXT    NOT NULL PRIMARY KEY,
            channel_id TEXT    NOT NULL,
            author_id  TEXT    NOT NULL,
            device_id  TEXT    NULL,
            epoch      INTEGER NOT NULL,
            ciphertext BLOB    NULL,
            sent_at    TEXT    NOT NULL,
            deleted_at TEXT    NULL,
            deleted_by TEXT    NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_encrypted_messages_history
            ON raindrop_encrypted_messages (channel_id, sent_at, message_id)
        """,

        // Reported messages: the text of an encrypted message a member chose to reveal, proven by
        // its author's attestation. The only encrypted content the server ever holds.
        """
        CREATE TABLE IF NOT EXISTS raindrop_message_reports (
            report_id   TEXT NOT NULL PRIMARY KEY,
            message_id  TEXT NOT NULL,
            channel_id  TEXT NOT NULL,
            author_id   TEXT NOT NULL,
            device_id   TEXT NOT NULL,
            reporter_id TEXT NOT NULL,
            body        TEXT NOT NULL,
            reason      TEXT NULL,
            sent_at     TEXT NOT NULL,
            reported_at TEXT NOT NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_message_reports_channel
            ON raindrop_message_reports (channel_id, reported_at)
        """,

        // Attachments: encrypted files added to messages. The server holds ciphertext and knows only
        // who uploaded it, for which channel, its declared extension and size, and which message
        // carries it; the bytes live in the attachment storage, not here.
        """
        CREATE TABLE IF NOT EXISTS raindrop_attachments (
            attachment_id      TEXT    NOT NULL PRIMARY KEY,
            channel_id         TEXT    NOT NULL,
            uploader_id        TEXT    NOT NULL,
            uploader_device_id TEXT    NOT NULL,
            kind               TEXT    NOT NULL,
            extension          TEXT    NOT NULL,
            ciphertext_size    INTEGER NOT NULL,
            received_size      INTEGER NOT NULL,
            ciphertext_sha256  BLOB    NULL,
            state              TEXT    NOT NULL,
            message_id         TEXT    NULL,
            created_at         TEXT    NOT NULL,
            completed_at       TEXT    NULL,
            deleted_at         TEXT    NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_attachments_message
            ON raindrop_attachments (message_id)
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_attachments_channel
            ON raindrop_attachments (channel_id)
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_attachments_state
            ON raindrop_attachments (state, created_at)
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_attachments_uploader
            ON raindrop_attachments (uploader_id, state)
        """
    ];
}
