using System.Data.Common;
using RaindropServer.Roles;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="IRoleStore"/> every SQL driver shares, written to the same rules as
/// <see cref="RelationalUserStore"/>: named <c>@parameters</c>, no dialect functions, and UUIDs
/// stored as 36-character text so a database stays portable between the three engines.
/// </summary>
/// <remarks>
/// Three tables: the roles, the permission names each one grants, and who holds them. None has
/// foreign keys, matching the rest of the schema — a message keeps pointing at the UUID of an erased
/// author, and the code, not the database, is what keeps the rows consistent. Deleting a role
/// therefore deletes its permissions and assignments explicitly.
/// <para>
/// Permissions are stored as names, one row each, so that a newer or modified build's permissions
/// survive this one saving the role, and so that two builds can never disagree about what a stored
/// value means. The old <c>permissions</c> flags column is still written with the bits this build
/// knows, so a server downgraded to a build from before names existed keeps working; a role with no
/// name rows at all — one written by such a build — is read from that column instead.
/// </para>
/// </remarks>
internal sealed class RelationalRoleStore : RelationalStore, IRoleStore
{
    /// <summary>The role table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_roles";

    /// <summary>The assignment table name, shared by the DDL in each driver.</summary>
    public const string AssignmentTable = "raindrop_member_roles";

    /// <summary>The applied-rollout table name, shared by the DDL in each driver.</summary>
    public const string RolloutTable = "raindrop_permission_rollouts";

    /// <summary>The table of permission names each role grants, shared by the DDL in each driver.</summary>
    public const string PermissionTable = "raindrop_role_permissions";

    private const string Columns = "role_id, name, name_normalized, permissions, sort_order, kind, created_at";

    private const string SelectSql = $"SELECT {Columns} FROM {Table}";

    /// <summary>
    /// Highest position first, then by name so that two roles sharing a position have a stable order
    /// rather than whichever one the database happened to reach first. The column is
    /// <c>sort_order</c> rather than <c>position</c>, which PostgreSQL treats as a keyword.
    /// </summary>
    private const string OrderSql = " ORDER BY sort_order DESC, name_normalized";

    private const string InsertSql =
        $"INSERT INTO {Table} ({Columns}) VALUES " +
        "(@role_id, @name, @name_normalized, @permissions, @sort_order, @kind, @created_at)";

    private const string UpdateSql =
        $"UPDATE {Table} SET name = @name, name_normalized = @name_normalized, " +
        "permissions = @permissions, sort_order = @sort_order, kind = @kind, created_at = @created_at " +
        "WHERE role_id = @role_id";

    public RelationalRoleStore(RelationalDatabaseDriver driver, string connectionString)
        : base(driver, connectionString)
    {
    }

    // ── The roles themselves ─────────────────────────────────────────────────

    public async Task<RoleRecord?> FindByIdAsync(Guid roleId, CancellationToken cancellationToken) =>
        await WithNamesAsync(await FindOneAsync($"{SelectSql} WHERE role_id = @role_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            Read,
            cancellationToken), cancellationToken);

    public async Task<RoleRecord?> FindByNameAsync(string nameNormalized, CancellationToken cancellationToken) =>
        await WithNamesAsync(await FindOneAsync($"{SelectSql} WHERE name_normalized = @name_normalized",
            command => AddValue(command, "@name_normalized", nameNormalized),
            Read,
            cancellationToken), cancellationToken);

    public async Task<RoleRecord?> FindByKindAsync(RoleKind kind, CancellationToken cancellationToken) =>
        await WithNamesAsync(await FindOneAsync($"{SelectSql} WHERE kind = @kind{OrderSql}",
            command => AddValue(command, "@kind", (int)kind),
            Read,
            cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<RoleRecord>> ListAsync(CancellationToken cancellationToken) =>
        await WithNamesAsync(await ReadManyAsync($"{SelectSql}{OrderSql}", _ => { }, Read, cancellationToken),
            cancellationToken);

    public async Task InsertAsync(RoleRecord role, CancellationToken cancellationToken)
    {
        try
        {
            await InTransactionAsync(async (connection, transaction) =>
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = InsertSql;
                    Bind(command, role);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await WriteNamesAsync(connection, transaction, role, cancellationToken);
                return true;
            }, cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, role, cancellationToken);
        }
    }

    public async Task UpdateAsync(RoleRecord role, CancellationToken cancellationToken)
    {
        int affected;
        try
        {
            affected = await InTransactionAsync(async (connection, transaction) =>
            {
                int rows;
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = UpdateSql;
                    Bind(command, role);
                    rows = await command.ExecuteNonQueryAsync(cancellationToken);
                }

                if (rows > 0)
                    await WriteNamesAsync(connection, transaction, role, cancellationToken);

                return rows;
            }, cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, role, cancellationToken);
        }

        if (affected == 0)
            throw new InvalidOperationException($"No role row exists for id {role.RoleId}.");
    }

    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken)
    {
        // Assignments go first: a row pointing at a role that is gone would grant nothing but would
        // still be listed against a member, and there are no foreign keys to do this for us.
        await ExecuteAsync($"DELETE FROM {AssignmentTable} WHERE role_id = @role_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            cancellationToken);

        await ExecuteAsync($"DELETE FROM {PermissionTable} WHERE role_id = @role_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            cancellationToken);

        await ExecuteAsync($"DELETE FROM {Table} WHERE role_id = @role_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            cancellationToken);
    }

    // ── Who holds them ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RoleRecord>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await WithNamesAsync(await ReadManyAsync(
            $"SELECT r.role_id, r.name, r.name_normalized, r.permissions, r.sort_order, r.kind, r.created_at " +
            $"FROM {Table} r INNER JOIN {AssignmentTable} m ON m.role_id = r.role_id " +
            "WHERE m.user_id = @user_id ORDER BY r.sort_order DESC, r.name_normalized",
            command => AddValue(command, "@user_id", userId.ToString()),
            Read,
            cancellationToken), cancellationToken);

    public Task<IReadOnlyList<Guid>> ListMembersAsync(Guid roleId, CancellationToken cancellationToken) =>
        ReadManyAsync(
            $"SELECT user_id FROM {AssignmentTable} WHERE role_id = @role_id ORDER BY assigned_at, user_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            reader => Guid.Parse(reader.GetString(0)),
            cancellationToken);

    public async Task<bool> AssignAsync(RoleAssignment assignment, CancellationToken cancellationToken)
    {
        // Asked first so that granting a role somebody already has reports "no change" rather than
        // failing on the primary key. The insert below is still what makes it true: two callers
        // racing here leave one of them catching the constraint, which is also "no change".
        if (await HoldsAsync(assignment.UserId, assignment.RoleId, cancellationToken))
            return false;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {AssignmentTable} (user_id, role_id, assigned_at, assigned_by) " +
                "VALUES (@user_id, @role_id, @assigned_at, @assigned_by)",
                command =>
                {
                    AddValue(command, "@user_id", assignment.UserId.ToString());
                    AddValue(command, "@role_id", assignment.RoleId.ToString());
                    AddTimestamp(command, "@assigned_at", assignment.AssignedAtUtc);
                    AddValue(command, "@assigned_by", assignment.AssignedBy?.ToString());
                },
                cancellationToken);

            return true;
        }
        catch (DbException)
        {
            // A caller racing us inserted the same pair first. The primary key is what actually keeps
            // one assignment per member and role, and losing that race still means nothing changed.
            if (await HoldsAsync(assignment.UserId, assignment.RoleId, cancellationToken))
                return false;

            throw;
        }
    }

    public async Task<bool> UnassignAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        var affected = await ExecuteAsync(
            $"DELETE FROM {AssignmentTable} WHERE user_id = @user_id AND role_id = @role_id",
            command =>
            {
                AddValue(command, "@user_id", userId.ToString());
                AddValue(command, "@role_id", roleId.ToString());
            },
            cancellationToken);

        return affected > 0;
    }

    // ── One-time permission rollouts ─────────────────────────────────────────

    public async Task<IReadOnlySet<string>> ListAppliedRolloutsAsync(CancellationToken cancellationToken)
    {
        var ids = await ReadManyAsync($"SELECT rollout_id FROM {RolloutTable}", _ => { },
            reader => reader.GetString(0), cancellationToken);

        return ids.ToHashSet(StringComparer.Ordinal);
    }

    public async Task MarkRolloutAppliedAsync(string rolloutId, DateTime appliedAtUtc, CancellationToken cancellationToken)
    {
        if ((await ListAppliedRolloutsAsync(cancellationToken)).Contains(rolloutId))
            return;

        try
        {
            await ExecuteAsync($"INSERT INTO {RolloutTable} (rollout_id, applied_at) VALUES (@rollout_id, @applied_at)",
                command =>
                {
                    AddValue(command, "@rollout_id", rolloutId);
                    AddTimestamp(command, "@applied_at", appliedAtUtc);
                },
                cancellationToken);
        }
        catch (DbException)
        {
            // Another process marked it first; the primary key is what keeps it to one row.
            if (!(await ListAppliedRolloutsAsync(cancellationToken)).Contains(rolloutId))
                throw;
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<bool> HoldsAsync(Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            $"SELECT COUNT(*) FROM {AssignmentTable} WHERE user_id = @user_id AND role_id = @role_id";
        AddValue(command, "@user_id", userId.ToString());
        AddValue(command, "@role_id", roleId.ToString());

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    /// <summary>Fills in one role's permissions from its name rows. See <see cref="WithNamesAsync(IReadOnlyList{RoleRecord}, CancellationToken)"/>.</summary>
    private async Task<RoleRecord?> WithNamesAsync(RoleRecord? role, CancellationToken cancellationToken)
    {
        if (role is null)
            return null;

        return (await WithNamesAsync([role], cancellationToken))[0];
    }

    /// <summary>
    /// Replaces the permissions each role was read with by its name rows: the names this build knows
    /// become flags, the rest are kept as <see cref="RoleRecord.UnknownPermissions"/>. A role with no
    /// name rows keeps what <see cref="Read"/> took from the old flags column — it was written before
    /// names were stored, or by a build that did not store them. Role tables are small, so a list
    /// reads every name in one query rather than one per role.
    /// </summary>
    private async Task<IReadOnlyList<RoleRecord>> WithNamesAsync(
        IReadOnlyList<RoleRecord> roles, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
            return roles;

        var rows = roles.Count == 1
            ? await ReadManyAsync($"SELECT role_id, permission FROM {PermissionTable} WHERE role_id = @role_id",
                command => AddValue(command, "@role_id", roles[0].RoleId.ToString()),
                ReadName,
                cancellationToken)
            : await ReadManyAsync($"SELECT role_id, permission FROM {PermissionTable}", _ => { },
                ReadName,
                cancellationToken);

        var byRole = rows.GroupBy(row => row.RoleId).ToDictionary(group => group.Key, group => group.Select(row => row.Name));

        return roles.Select(role =>
        {
            if (!byRole.TryGetValue(role.RoleId, out var names))
                return role;

            var (known, unknown) = PermissionNames.Split(names);
            return role with { Permissions = known, UnknownPermissions = unknown };
        }).ToList();
    }

    private static (Guid RoleId, string Name) ReadName(DbDataReader reader) =>
        (Guid.Parse(reader.GetString(0)), reader.GetString(1));

    /// <summary>
    /// Replaces a role's name rows, inside the transaction that writes the role itself: every name
    /// this build knows that the role grants, and every stored name it does not know, untouched.
    /// </summary>
    private async Task WriteNamesAsync(
        DbConnection connection, DbTransaction transaction, RoleRecord role, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {PermissionTable} WHERE role_id = @role_id";
            AddValue(delete, "@role_id", role.RoleId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var names = PermissionNames.Of(role.Permissions)
            .Concat(role.UnknownPermissions)
            .Distinct(StringComparer.Ordinal);

        foreach (var name in names)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {PermissionTable} (role_id, permission) VALUES (@role_id, @permission)";
            AddValue(insert, "@role_id", role.RoleId.ToString());
            AddValue(insert, "@permission", name);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private RoleRecord Read(DbDataReader reader) => new()
    {
        RoleId = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        NameNormalized = reader.GetString(2),
        // The old flags column, read through the catalogue so a bit this build has no name for grants
        // nothing. WithNamesAsync replaces it with the role's name rows whenever it has any.
        Permissions = PermissionNames.FromStorage(reader.GetInt64(3)),
        Position = reader.GetInt32(4),
        Kind = (RoleKind)reader.GetInt32(5),
        CreatedAtUtc = Driver.ReadTimestamp(reader, 6)
    };

    private void Bind(DbCommand command, RoleRecord role)
    {
        AddValue(command, "@role_id", role.RoleId.ToString());
        AddValue(command, "@name", role.Name);
        AddValue(command, "@name_normalized", role.NameNormalized);
        // Still written, with the bits this build knows, for a server downgraded to a build that reads
        // only this column. The name rows are what this build reads.
        AddValue(command, "@permissions", (long)role.Permissions);
        AddValue(command, "@sort_order", role.Position);
        AddValue(command, "@kind", (int)role.Kind);
        AddTimestamp(command, "@created_at", role.CreatedAtUtc);
    }

    /// <summary>
    /// Turns the unique-name constraint into the API error it really is. The service checks the name
    /// before claiming it, but two clients can still race between the check and the insert — the
    /// unique index is what actually keeps names unique, and this is how that surfaces.
    /// </summary>
    private async Task<Exception> TranslateWriteFailureAsync(
        DbException ex, RoleRecord role, CancellationToken cancellationToken)
    {
        var owner = await FindByNameAsync(role.NameNormalized, cancellationToken);

        if (owner is not null && owner.RoleId != role.RoleId)
            return ApiException.Conflict("role_name_taken", "A role with that name already exists on this server.");

        return ex;
    }
}
