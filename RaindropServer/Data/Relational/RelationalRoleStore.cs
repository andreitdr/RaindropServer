using System.Data.Common;
using RaindropServer.Roles;

namespace RaindropServer.Data.Relational;

/// <summary>
/// The one <see cref="IRoleStore"/> every SQL driver shares, written to the same rules as
/// <see cref="RelationalUserStore"/>: named <c>@parameters</c>, no dialect functions, and UUIDs
/// stored as 36-character text so a database stays portable between the three engines.
/// </summary>
/// <remarks>
/// Two tables: the roles, and who holds them. The assignment table has no foreign keys, matching the
/// rest of the schema — a message keeps pointing at the UUID of an erased author, and the code, not
/// the database, is what keeps the rows consistent. Deleting a role therefore deletes its
/// assignments explicitly.
/// </remarks>
internal sealed class RelationalRoleStore : RelationalStore, IRoleStore
{
    /// <summary>The role table name, shared by the DDL in each driver.</summary>
    public const string Table = "raindrop_roles";

    /// <summary>The assignment table name, shared by the DDL in each driver.</summary>
    public const string AssignmentTable = "raindrop_member_roles";

    /// <summary>The applied-rollout table name, shared by the DDL in each driver.</summary>
    public const string RolloutTable = "raindrop_permission_rollouts";

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

    public Task<RoleRecord?> FindByIdAsync(Guid roleId, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE role_id = @role_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            Read,
            cancellationToken);

    public Task<RoleRecord?> FindByNameAsync(string nameNormalized, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE name_normalized = @name_normalized",
            command => AddValue(command, "@name_normalized", nameNormalized),
            Read,
            cancellationToken);

    public Task<RoleRecord?> FindByKindAsync(RoleKind kind, CancellationToken cancellationToken) =>
        FindOneAsync($"{SelectSql} WHERE kind = @kind{OrderSql}",
            command => AddValue(command, "@kind", (int)kind),
            Read,
            cancellationToken);

    public Task<IReadOnlyList<RoleRecord>> ListAsync(CancellationToken cancellationToken) =>
        ReadManyAsync($"{SelectSql}{OrderSql}", _ => { }, Read, cancellationToken);

    public async Task InsertAsync(RoleRecord role, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = InsertSql;
        Bind(command, role);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            throw await TranslateWriteFailureAsync(ex, role, cancellationToken);
        }
    }

    public async Task UpdateAsync(RoleRecord role, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText = UpdateSql;
        Bind(command, role);

        int affected;
        try
        {
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
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

        await ExecuteAsync($"DELETE FROM {Table} WHERE role_id = @role_id",
            command => AddValue(command, "@role_id", roleId.ToString()),
            cancellationToken);
    }

    // ── Who holds them ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<RoleRecord>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        ReadManyAsync(
            $"SELECT r.role_id, r.name, r.name_normalized, r.permissions, r.sort_order, r.kind, r.created_at " +
            $"FROM {Table} r INNER JOIN {AssignmentTable} m ON m.role_id = r.role_id " +
            "WHERE m.user_id = @user_id ORDER BY r.sort_order DESC, r.name_normalized",
            command => AddValue(command, "@user_id", userId.ToString()),
            Read,
            cancellationToken);

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

    private RoleRecord Read(DbDataReader reader) => new()
    {
        RoleId = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        NameNormalized = reader.GetString(2),
        // Read through the catalogue so a database written by a newer build cannot hand this one a
        // permission bit it has no name for.
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
