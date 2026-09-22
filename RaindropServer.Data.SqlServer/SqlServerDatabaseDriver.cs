using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using RaindropServer.Data.Relational;

namespace RaindropServer.Data.SqlServer;

/// <summary>
/// The Microsoft SQL Server database module — enabled with <c>"provider": "sqlserver"</c>. Aimed at
/// server owners hosting inside an organisation that already runs SQL Server.
/// </summary>
/// <remarks>
/// Example connection string:
/// <c>Server=localhost,1433;Database=raindrop;User Id=raindrop;Password=…;TrustServerCertificate=True</c>.
/// </remarks>
public sealed class SqlServerDatabaseDriver : RelationalDatabaseDriver
{
    public override string Name => "sqlserver";

    protected override DbConnection CreateConnection(string connectionString) =>
        new SqlConnection(connectionString);

    /// <summary>
    /// Without this, SqlClient would infer the legacy <c>datetime</c> type for a DateTime and round
    /// the value to the nearest 3⅓ milliseconds on its way into a <c>datetime2</c> column.
    /// </summary>
    protected override void BindTimestamp(DbParameter parameter, DateTime utc)
    {
        parameter.DbType = DbType.DateTime2;
        parameter.Value = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// SQL Server has no <c>LIMIT</c>; the standard <c>OFFSET … FETCH</c> is the way to take the top
    /// rows of an ordered query, and it takes a parameter for the count like any other expression.
    /// </summary>
    protected override string ApplyRowLimit(string orderedSql, string parameterName) =>
        $"{orderedSql} OFFSET 0 ROWS FETCH NEXT {parameterName} ROWS ONLY";

    protected override IReadOnlyList<string> SchemaStatements =>
    [
        // The objects are created in the connection's default schema, so a server owner can point
        // Raindrop at a schema of their own without editing anything here.
        """
        IF OBJECT_ID(N'raindrop_users', N'U') IS NULL
        CREATE TABLE raindrop_users (
            user_id             varchar(36)   NOT NULL PRIMARY KEY,
            username            nvarchar(32)  NULL,
            username_normalized nvarchar(32)  NULL,
            password_hash       nvarchar(256) NULL,
            status              int           NOT NULL,
            created_at          datetime2     NOT NULL,
            last_login_at       datetime2     NULL,
            left_at             datetime2     NULL,
            data_expires_at     datetime2     NULL
        )
        """,

        // SQL Server would otherwise treat two NULL usernames as duplicates, so the index is filtered
        // down to the accounts that actually hold a name.
        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ux_raindrop_users_username' AND object_id = OBJECT_ID(N'raindrop_users'))
        CREATE UNIQUE INDEX ux_raindrop_users_username
            ON raindrop_users (username_normalized)
            WHERE username_normalized IS NOT NULL
        """,

        """
        IF OBJECT_ID(N'raindrop_channels', N'U') IS NULL
        CREATE TABLE raindrop_channels (
            channel_id      varchar(36)  NOT NULL PRIMARY KEY,
            channel_type    int          NOT NULL,
            name            nvarchar(64) NOT NULL,
            name_normalized nvarchar(64) NOT NULL,
            created_by      varchar(36)  NOT NULL,
            created_at      datetime2    NOT NULL
        )
        """,

        // Channel names are unique per server, so "#general" means one place to everybody here.
        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ux_raindrop_channels_name' AND object_id = OBJECT_ID(N'raindrop_channels'))
        CREATE UNIQUE INDEX ux_raindrop_channels_name
            ON raindrop_channels (name_normalized)
        """,

        """
        IF OBJECT_ID(N'raindrop_roles', N'U') IS NULL
        CREATE TABLE raindrop_roles (
            role_id         varchar(36)  NOT NULL PRIMARY KEY,
            name            nvarchar(64) NOT NULL,
            name_normalized nvarchar(64) NOT NULL,
            permissions     bigint       NOT NULL,
            sort_order      int          NOT NULL,
            kind            int          NOT NULL,
            created_at      datetime2    NOT NULL
        )
        """,

        // Role names are unique per server, so "give them Moderator" means one thing to everybody.
        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ux_raindrop_roles_name' AND object_id = OBJECT_ID(N'raindrop_roles'))
        CREATE UNIQUE INDEX ux_raindrop_roles_name
            ON raindrop_roles (name_normalized)
        """,

        """
        IF OBJECT_ID(N'raindrop_member_roles', N'U') IS NULL
        CREATE TABLE raindrop_member_roles (
            user_id     varchar(36) NOT NULL,
            role_id     varchar(36) NOT NULL,
            assigned_at datetime2   NOT NULL,
            assigned_by varchar(36) NULL,
            PRIMARY KEY (user_id, role_id)
        )
        """,

        // The primary key already answers "what does this member hold"; this is the other direction,
        // which every permission change needs in order to find who was affected.
        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_member_roles_role' AND object_id = OBJECT_ID(N'raindrop_member_roles'))
        CREATE INDEX ix_raindrop_member_roles_role
            ON raindrop_member_roles (role_id)
        """,

        """
        IF OBJECT_ID(N'raindrop_text_messages', N'U') IS NULL
        CREATE TABLE raindrop_text_messages (
            message_id varchar(36)    NOT NULL PRIMARY KEY,
            channel_id varchar(36)    NOT NULL,
            author_id  varchar(36)    NOT NULL,
            content    nvarchar(max)  NULL,
            sent_at    datetime2      NOT NULL,
            deleted_at datetime2      NULL,
            deleted_by varchar(36)    NULL
        )
        """,

        // The one query that matters: a channel's history, newest first. The columns are in the order
        // the paging cursor compares them, so a page is a range scan rather than a sort.
        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_text_messages_history' AND object_id = OBJECT_ID(N'raindrop_text_messages'))
        CREATE INDEX ix_raindrop_text_messages_history
            ON raindrop_text_messages (channel_id, sent_at, message_id)
        """,

        // The original text of deleted messages, kept for the server owner. No foreign keys, like
        // everything else here: the channel it names may well have been deleted.
        """
        IF OBJECT_ID(N'raindrop_deleted_messages', N'U') IS NULL
        CREATE TABLE raindrop_deleted_messages (
            message_id   varchar(36)    NOT NULL PRIMARY KEY,
            channel_id   varchar(36)    NOT NULL,
            channel_name nvarchar(256)  NOT NULL,
            author_id    varchar(36)    NOT NULL,
            content      nvarchar(max)  NOT NULL,
            sent_at      datetime2      NOT NULL,
            deleted_at   datetime2      NOT NULL,
            deleted_by   varchar(36)    NOT NULL,
            reason       varchar(64)    NOT NULL
        )
        """,

        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_deleted_messages_channel' AND object_id = OBJECT_ID(N'raindrop_deleted_messages'))
        CREATE INDEX ix_raindrop_deleted_messages_channel
            ON raindrop_deleted_messages (channel_id, deleted_at)
        """,

        // Which one-time permission rollouts this database has had — see PermissionRollouts. A row
        // per rollout, so a permission added by a later build reaches the everyone role exactly once.
        """
        IF OBJECT_ID(N'raindrop_permission_rollouts', N'U') IS NULL
        CREATE TABLE raindrop_permission_rollouts (
            rollout_id varchar(64) NOT NULL PRIMARY KEY,
            applied_at datetime2   NOT NULL
        )
        """,

        // Members muted in every voice call by a moderator ("mute for everyone"). One row per muted
        // member; lifting the mute deletes it.
        """
        IF OBJECT_ID(N'raindrop_voice_mutes', N'U') IS NULL
        CREATE TABLE raindrop_voice_mutes (
            user_id  varchar(36) NOT NULL PRIMARY KEY,
            muted_by varchar(36) NOT NULL,
            muted_at datetime2   NOT NULL
        )
        """,

        // End-to-end encryption: users' public identity keys, their devices, and sealed key backups.
        """
        IF OBJECT_ID(N'raindrop_user_identities', N'U') IS NULL
        CREATE TABLE raindrop_user_identities (
            user_id      varchar(36)    NOT NULL PRIMARY KEY,
            identity_key varbinary(64)  NOT NULL,
            updated_at   datetime2      NOT NULL
        )
        """,

        """
        IF OBJECT_ID(N'raindrop_devices', N'U') IS NULL
        CREATE TABLE raindrop_devices (
            user_id       varchar(36)    NOT NULL,
            device_id     varchar(36)    NOT NULL,
            signature_key varbinary(64)  NOT NULL,
            certificate   varbinary(1024) NOT NULL,
            created_at    datetime2      NOT NULL,
            revoked_at    datetime2      NULL,
            PRIMARY KEY (user_id, device_id)
        )
        """,

        """
        IF OBJECT_ID(N'raindrop_key_backups', N'U') IS NULL
        CREATE TABLE raindrop_key_backups (
            user_id    varchar(36)    NOT NULL PRIMARY KEY,
            blob_data  varbinary(max) NOT NULL,
            updated_at datetime2      NOT NULL
        )
        """,

        // The MLS delivery service: groups, commit logs, key packages and pending Welcomes.
        """
        IF OBJECT_ID(N'raindrop_mls_groups', N'U') IS NULL
        CREATE TABLE raindrop_mls_groups (
            channel_id       varchar(36)    NOT NULL PRIMARY KEY,
            epoch            bigint         NOT NULL,
            group_info       varbinary(max) NULL,
            group_info_epoch bigint         NOT NULL,
            updated_at       datetime2      NOT NULL
        )
        """,

        """
        IF OBJECT_ID(N'raindrop_mls_commits', N'U') IS NULL
        CREATE TABLE raindrop_mls_commits (
            channel_id     varchar(36)    NOT NULL,
            epoch          bigint         NOT NULL,
            message        varbinary(max) NOT NULL,
            sender_user_id varchar(36)    NOT NULL,
            created_at     datetime2      NOT NULL,
            PRIMARY KEY (channel_id, epoch)
        )
        """,

        """
        IF OBJECT_ID(N'raindrop_mls_key_packages', N'U') IS NULL
        CREATE TABLE raindrop_mls_key_packages (
            package_id  varchar(36)    NOT NULL PRIMARY KEY,
            user_id     varchar(36)    NOT NULL,
            device_id   varchar(36)    NOT NULL,
            key_package varbinary(max) NOT NULL,
            last_resort int            NOT NULL,
            created_at  datetime2      NOT NULL
        )
        """,

        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_mls_key_packages_device' AND object_id = OBJECT_ID(N'raindrop_mls_key_packages'))
        CREATE INDEX ix_raindrop_mls_key_packages_device
            ON raindrop_mls_key_packages (user_id, device_id, last_resort, created_at)
        """,

        """
        IF OBJECT_ID(N'raindrop_mls_welcomes', N'U') IS NULL
        CREATE TABLE raindrop_mls_welcomes (
            welcome_id varchar(36)    NOT NULL PRIMARY KEY,
            user_id    varchar(36)    NOT NULL,
            device_id  varchar(36)    NOT NULL,
            channel_id varchar(36)    NOT NULL,
            epoch      bigint         NOT NULL,
            welcome    varbinary(max) NOT NULL,
            created_at datetime2      NOT NULL
        )
        """,

        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_mls_welcomes_device' AND object_id = OBJECT_ID(N'raindrop_mls_welcomes'))
        CREATE INDEX ix_raindrop_mls_welcomes_device
            ON raindrop_mls_welcomes (user_id, device_id)
        """,

        // End-to-end encrypted messages: MLS ciphertext the server cannot read.
        """
        IF OBJECT_ID(N'raindrop_encrypted_messages', N'U') IS NULL
        CREATE TABLE raindrop_encrypted_messages (
            message_id varchar(36)    NOT NULL PRIMARY KEY,
            channel_id varchar(36)    NOT NULL,
            author_id  varchar(36)    NOT NULL,
            device_id  varchar(36)    NULL,
            epoch      bigint         NOT NULL,
            ciphertext varbinary(max) NULL,
            sent_at    datetime2      NOT NULL,
            deleted_at datetime2      NULL,
            deleted_by varchar(36)    NULL
        )
        """,

        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_encrypted_messages_history' AND object_id = OBJECT_ID(N'raindrop_encrypted_messages'))
        CREATE INDEX ix_raindrop_encrypted_messages_history
            ON raindrop_encrypted_messages (channel_id, sent_at, message_id)
        """,

        // Reported messages: the text of an encrypted message a member chose to reveal.
        """
        IF OBJECT_ID(N'raindrop_message_reports', N'U') IS NULL
        CREATE TABLE raindrop_message_reports (
            report_id   varchar(36)   NOT NULL PRIMARY KEY,
            message_id  varchar(36)   NOT NULL,
            channel_id  varchar(36)   NOT NULL,
            author_id   varchar(36)   NOT NULL,
            device_id   varchar(36)   NOT NULL,
            reporter_id varchar(36)   NOT NULL,
            body        nvarchar(max) NOT NULL,
            reason      nvarchar(500) NULL,
            sent_at     datetime2     NOT NULL,
            reported_at datetime2     NOT NULL
        )
        """,

        """
        IF NOT EXISTS (
            SELECT 1 FROM sys.indexes
            WHERE name = 'ix_raindrop_message_reports_channel' AND object_id = OBJECT_ID(N'raindrop_message_reports'))
        CREATE INDEX ix_raindrop_message_reports_channel
            ON raindrop_message_reports (channel_id, reported_at)
        """
    ];
}
