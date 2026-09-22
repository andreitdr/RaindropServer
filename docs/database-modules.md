# Database modules

## The contract

[`IDatabaseDriver`](../RaindropServer/Data/IDatabaseDriver.cs) is the whole contract a database module
implements: a `Name`, a store per kind of data (`IUserStore`, `IChannelStore`, `IRoleStore`,
`ITextMessageStore`, `IDeletedMessageStore`, `IVoiceMuteStore`, and for end-to-end encryption
`IDeviceStore` and `IKeyBackupStore`), and idempotent schema DDL (`EnsureSchemaAsync` — must be safe to run on every
startup, since it runs unless `DatabaseOptions.AutoCreateSchema` is turned off).

## Discovery

[`DatabaseDriverRegistry`](../RaindropServer/Data/DatabaseDriverRegistry.cs) discovers drivers from
three places, cached after the first lookup:

1. Assemblies already loaded into the process.
2. Files named `RaindropServer.Data.*.dll` sitting next to the executable.
3. Any `*.dll` in a `drivers/` folder next to the executable — this is how a **third-party driver**
   gets added without touching this repository at all.

A driver is only ever *used* when the server owner names it in `database.provider` in configuration
(see [Configuration](configuration.md#database)) — so a SQLite server never even opens the Postgres or
SQL Server client libraries, because their assemblies are never loaded in the first place.
`DatabaseDriverRegistry.Resolve(name)` looks the driver up case-insensitively and throws, listing what
*is* installed, if the name doesn't match exactly one.

## The relational store family

In practice, a driver derives from
[`RelationalDatabaseDriver`](../RaindropServer/Data/Relational/RelationalDatabaseDriver.cs) and
supplies three things:

- a `DbConnection` factory,
- the DDL for its SQL dialect, and
- (where providers disagree) how a timestamp is bound to a parameter.

Everything else lives in the shared `Relational*Store` family under
[`RaindropServer/Data/Relational/`](../RaindropServer/Data/Relational/):

- `RelationalStore.cs` — the ADO.NET plumbing every store shares.
- `RelationalUserStore.cs`, `RelationalChannelStore.cs`, `RelationalRoleStore.cs`,
  `RelationalTextMessageStore.cs` — the `I*Store` implementations.

Their SQL stays inside the common subset all three supported databases understand: named `@parameters`,
no dialect-specific functions. **UUIDs are stored as 36-character text**, not native GUID columns,
because SQLite, PostgreSQL and SQL Server disagree on GUID typing and byte order.

The one statement that couldn't stay in that common subset is the row limit message history needs:
SQLite and PostgreSQL take `LIMIT`, SQL Server wants `OFFSET … FETCH`. It's asked of the driver
through `ApplyRowLimit`, which `RaindropServer.Data.SqlServer` overrides and the other two inherit
unchanged.

### The three shipped drivers

| Driver | Assembly | Connection string example |
|---|---|---|
| SQLite (default) | [`RaindropServer.Data.Sqlite`](../RaindropServer.Data.Sqlite/) | `Data Source=raindrop.db` |
| PostgreSQL | [`RaindropServer.Data.Postgres`](../RaindropServer.Data.Postgres/) | `Host=localhost;Port=5432;Database=raindrop;Username=raindrop;Password=…` |
| SQL Server | [`RaindropServer.Data.SqlServer`](../RaindropServer.Data.SqlServer/) | `Server=localhost,1433;Database=raindrop;User Id=raindrop;Password=…;TrustServerCertificate=True` |

Each is one file: `SqliteDatabaseDriver.cs`, `PostgresDatabaseDriver.cs`, `SqlServerDatabaseDriver.cs`.

## Adding a driver

1. Derive from `RelationalDatabaseDriver`.
2. Override `CreateConnection` and `SchemaStatements` — both are declared `protected internal` on the
   base and should be overridden as `protected override` in the derived driver.
3. Give it a unique `Name`.
4. Add the DDL for every table, including `raindrop_deleted_messages` — a driver that leaves a table out
   will fail the first time something needs it. (A driver deriving from `RelationalDatabaseDriver` gets the
   store itself for free; it only supplies the per-dialect DDL.) `IDatabaseDriver` gained
   `CreateDeletedMessageStore` with the archive, so a third-party driver that implements the interface
   directly, rather than deriving from `RelationalDatabaseDriver`, has to add it.

## Schema

Tables: `raindrop_users`, `raindrop_channels`, `raindrop_text_messages`, `raindrop_roles`,
`raindrop_member_roles`, `raindrop_deleted_messages`, `raindrop_permission_rollouts`,
`raindrop_voice_mutes`, and for [end-to-end encryption](e2ee.md#storage) `raindrop_user_identities`,
`raindrop_devices`, `raindrop_key_backups` and the delivery service's `raindrop_mls_groups`,
`raindrop_mls_commits`, `raindrop_mls_key_packages`, `raindrop_mls_welcomes`, and
`raindrop_encrypted_messages` (encrypted messages, merged with `raindrop_text_messages` when history is
read), `raindrop_message_reports` (reported encrypted messages, with the text the reporter revealed) — public keys and opaque blobs in byte columns (`BLOB`, `bytea`, `varbinary`), epochs as `bigint`.

`CreateDeviceStore`, `CreateKeyBackupStore`, `CreateMlsGroupStore`, `CreateKeyPackageStore`,
`CreateWelcomeStore` and `CreateMessageReportStore` have **default interface implementations** that throw
`NotSupportedException`, so a third-party driver written before end-to-end encryption still loads; it
fails only when those stores are used. `RelationalDatabaseDriver` implements both, so a driver deriving
from it needs only their `CREATE TABLE` statements added to its DDL. The commit log's primary key
`(channel_id, epoch)` is load-bearing: it is what refuses a second commit for an epoch.
`RelationalStore.InTransactionAsync` runs the commit's epoch bump and log insert in one transaction.

`raindrop_permission_rollouts(rollout_id PK, applied_at)` records which one-time
[permission rollouts](roles-and-permissions.md#permission-rollouts) this database has had.
`raindrop_voice_mutes(user_id PK, muted_by, muted_at)` holds members muted in every voice call by a
moderator — the one piece of [voice](voice.md) state that survives a restart (who is in which call lives
in memory). Both were added as new tables, so existing databases gain them without an `ALTER`. A
third-party driver deriving from `RelationalDatabaseDriver` gets the stores for free but has to add the
two `CREATE TABLE` statements to its own DDL.

`raindrop_deleted_messages` holds the original text of every deleted message (see
[the deleted-message archive](messages.md#the-deleted-message-archive)): `message_id` is its primary key —
a message is archived at most once — beside `channel_id`, `channel_name`, `author_id`, `content`, `sent_at`,
`deleted_at`, `deleted_by` and `reason`, with an index on `(channel_id, deleted_at)`. It was added as a *new*
table, so an existing database gains it on the next start without an `ALTER`. Nothing in the server reads
it: it is there for the owner to query.

Notable design choices, all driven by the same constraint — **there is no migration mechanism**, so
`EnsureSchemaAsync` only ever creates what's *missing*; adding a column to a table that already exists
somewhere in the wild would need a guarded `ALTER TABLE` per dialect (which SQLite makes especially
awkward):

- `raindrop_roles.permissions` is a `bigint` holding a `[Flags]` enum (see
  [Roles & permissions](roles-and-permissions.md#permissions)) rather than a narrower column or a row
  per permission — the room to grow had to exist from day one, since there's no later migration to add
  it.
- The role table's position column is spelled `sort_order` rather than `position`, because `position`
  is a reserved keyword in PostgreSQL.
- **There are no foreign keys anywhere in the schema**, `raindrop_member_roles` included. A message
  keeps pointing at the UUID of an erased author (see [Users](users.md)); the application code, not
  the database, is what keeps rows consistent. Consequently, deleting a role explicitly deletes its
  assignment rows too, rather than relying on `ON DELETE CASCADE`.

## Related

- [Configuration](configuration.md#database) — the `database` section and `RAINDROP_DB_*` environment
  variables.
- [Local development & Docker](local-development-and-docker.md) — running Postgres/SQL Server locally
  via `docker-compose.yml`.
