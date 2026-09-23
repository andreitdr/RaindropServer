using System.Data.Common;
using Npgsql;
using RaindropServer.Data.Relational;

namespace RaindropServer.Data.Postgres;

/// <summary>
/// The PostgreSQL database module — enabled with <c>"provider": "postgres"</c>. The usual choice for
/// a server that expects real traffic or already has a Postgres instance to point at.
/// </summary>
/// <remarks>
/// Example connection string:
/// <c>Host=localhost;Port=5432;Database=raindrop;Username=raindrop;Password=…</c>.
/// </remarks>
public sealed class PostgresDatabaseDriver : RelationalDatabaseDriver
{
    public override string Name => "postgres";

    protected override DbConnection CreateConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);

    /// <summary>
    /// The columns are <c>timestamptz</c>, and Npgsql insists that a value bound to one be marked as
    /// UTC — the opposite of the timezone-less defaults the other drivers use.
    /// </summary>
    protected override void BindTimestamp(DbParameter parameter, DateTime utc) =>
        parameter.Value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

    protected override IReadOnlyList<string> SchemaStatements =>
    [
        """
        CREATE TABLE IF NOT EXISTS raindrop_users (
            user_id             varchar(36)  NOT NULL PRIMARY KEY,
            username            varchar(32)  NULL,
            username_normalized varchar(32)  NULL,
            password_hash       varchar(256) NULL,
            status              integer      NOT NULL,
            created_at          timestamptz  NOT NULL,
            last_login_at       timestamptz  NULL,
            left_at             timestamptz  NULL,
            data_expires_at     timestamptz  NULL
        )
        """,

        // PostgreSQL treats NULLs as distinct in a unique index, so the erased accounts — which give
        // up their username but keep their UUID row — coexist here without extra ceremony.
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_raindrop_users_username
            ON raindrop_users (username_normalized)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_channels (
            channel_id      varchar(36) NOT NULL PRIMARY KEY,
            channel_type    integer     NOT NULL,
            name            varchar(64) NOT NULL,
            name_normalized varchar(64) NOT NULL,
            created_by      varchar(36) NOT NULL,
            created_at      timestamptz NOT NULL
        )
        """,

        // Channel names are unique per server, so "#general" means one place to everybody here.
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_raindrop_channels_name
            ON raindrop_channels (name_normalized)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_roles (
            role_id         varchar(36) NOT NULL PRIMARY KEY,
            name            varchar(64) NOT NULL,
            name_normalized varchar(64) NOT NULL,
            permissions     bigint      NOT NULL,
            sort_order      integer     NOT NULL,
            kind            integer     NOT NULL,
            created_at      timestamptz NOT NULL
        )
        """,

        // Role names are unique per server, so "give them Moderator" means one thing to everybody.
        """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_raindrop_roles_name
            ON raindrop_roles (name_normalized)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_member_roles (
            user_id     varchar(36) NOT NULL,
            role_id     varchar(36) NOT NULL,
            assigned_at timestamptz NOT NULL,
            assigned_by varchar(36) NULL,
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
            message_id varchar(36) NOT NULL PRIMARY KEY,
            channel_id varchar(36) NOT NULL,
            author_id  varchar(36) NOT NULL,
            content    text        NULL,
            sent_at    timestamptz NOT NULL,
            deleted_at timestamptz NULL,
            deleted_by varchar(36) NULL
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
            message_id   varchar(36)  NOT NULL PRIMARY KEY,
            channel_id   varchar(36)  NOT NULL,
            channel_name varchar(256) NOT NULL,
            author_id    varchar(36)  NOT NULL,
            content      text         NOT NULL,
            sent_at      timestamptz  NOT NULL,
            deleted_at   timestamptz  NOT NULL,
            deleted_by   varchar(36)  NOT NULL,
            reason       varchar(64)  NOT NULL
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
            rollout_id varchar(64) NOT NULL PRIMARY KEY,
            applied_at timestamptz NOT NULL
        )
        """,

        // Members muted in every voice call by a moderator ("mute for everyone"). One row per muted
        // member; lifting the mute deletes it.
        """
        CREATE TABLE IF NOT EXISTS raindrop_voice_mutes (
            user_id  varchar(36) NOT NULL PRIMARY KEY,
            muted_by varchar(36) NOT NULL,
            muted_at timestamptz NOT NULL
        )
        """,

        // End-to-end encryption: users' public identity keys, their devices, and sealed key backups.
        """
        CREATE TABLE IF NOT EXISTS raindrop_user_identities (
            user_id      varchar(36) NOT NULL PRIMARY KEY,
            identity_key bytea       NOT NULL,
            updated_at   timestamptz NOT NULL
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_devices (
            user_id       varchar(36) NOT NULL,
            device_id     varchar(36) NOT NULL,
            signature_key bytea       NOT NULL,
            certificate   bytea       NOT NULL,
            created_at    timestamptz NOT NULL,
            revoked_at    timestamptz NULL,
            PRIMARY KEY (user_id, device_id)
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_key_backups (
            user_id    varchar(36) NOT NULL PRIMARY KEY,
            blob_data  bytea       NOT NULL,
            updated_at timestamptz NOT NULL
        )
        """,

        // The MLS delivery service: groups, commit logs, key packages and pending Welcomes.
        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_groups (
            channel_id       varchar(36) NOT NULL PRIMARY KEY,
            epoch            bigint      NOT NULL,
            group_info       bytea       NULL,
            group_info_epoch bigint      NOT NULL,
            updated_at       timestamptz NOT NULL
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_commits (
            channel_id     varchar(36) NOT NULL,
            epoch          bigint      NOT NULL,
            message        bytea       NOT NULL,
            sender_user_id varchar(36) NOT NULL,
            created_at     timestamptz NOT NULL,
            PRIMARY KEY (channel_id, epoch)
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_key_packages (
            package_id  varchar(36) NOT NULL PRIMARY KEY,
            user_id     varchar(36) NOT NULL,
            device_id   varchar(36) NOT NULL,
            key_package bytea       NOT NULL,
            last_resort integer     NOT NULL,
            created_at  timestamptz NOT NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_mls_key_packages_device
            ON raindrop_mls_key_packages (user_id, device_id, last_resort, created_at)
        """,

        """
        CREATE TABLE IF NOT EXISTS raindrop_mls_welcomes (
            welcome_id varchar(36) NOT NULL PRIMARY KEY,
            user_id    varchar(36) NOT NULL,
            device_id  varchar(36) NOT NULL,
            channel_id varchar(36) NOT NULL,
            epoch      bigint      NOT NULL,
            welcome    bytea       NOT NULL,
            created_at timestamptz NOT NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_mls_welcomes_device
            ON raindrop_mls_welcomes (user_id, device_id)
        """,

        // End-to-end encrypted messages: MLS ciphertext the server cannot read.
        """
        CREATE TABLE IF NOT EXISTS raindrop_encrypted_messages (
            message_id varchar(36) NOT NULL PRIMARY KEY,
            channel_id varchar(36) NOT NULL,
            author_id  varchar(36) NOT NULL,
            device_id  varchar(36) NULL,
            epoch      bigint      NOT NULL,
            ciphertext bytea       NULL,
            sent_at    timestamptz NOT NULL,
            deleted_at timestamptz NULL,
            deleted_by varchar(36) NULL
        )
        """,

        """
        CREATE INDEX IF NOT EXISTS ix_raindrop_encrypted_messages_history
            ON raindrop_encrypted_messages (channel_id, sent_at, message_id)
        """,

        // Reported messages: the text of an encrypted message a member chose to reveal.
        """
        CREATE TABLE IF NOT EXISTS raindrop_message_reports (
            report_id   varchar(36) NOT NULL PRIMARY KEY,
            message_id  varchar(36) NOT NULL,
            channel_id  varchar(36) NOT NULL,
            author_id   varchar(36) NOT NULL,
            device_id   varchar(36) NOT NULL,
            reporter_id varchar(36) NOT NULL,
            body        text        NOT NULL,
            reason      text        NULL,
            sent_at     timestamptz NOT NULL,
            reported_at timestamptz NOT NULL
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
            attachment_id      varchar(36) NOT NULL PRIMARY KEY,
            channel_id         varchar(36) NOT NULL,
            uploader_id        varchar(36) NOT NULL,
            uploader_device_id varchar(36) NOT NULL,
            kind               varchar(32) NOT NULL,
            extension          varchar(16) NOT NULL,
            ciphertext_size    bigint      NOT NULL,
            received_size      bigint      NOT NULL,
            ciphertext_sha256  bytea       NULL,
            state              varchar(16) NOT NULL,
            message_id         varchar(36) NULL,
            created_at         timestamptz NOT NULL,
            completed_at       timestamptz NULL,
            deleted_at         timestamptz NULL
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
