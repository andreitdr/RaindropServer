using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using RaindropServer.Channels;
using RaindropServer.Data;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer.Tests;

/// <summary>
/// How roles are stored: by name, one row per permission, so a permission this build does not know is
/// kept rather than misread or erased. Runs on every database under test.
/// </summary>
public class RoleStoreTests
{
    private static RoleRecord Role(Permission permissions, params string[] unknown)
    {
        var name = $"r{Guid.NewGuid():N}"[..20];
        return new RoleRecord
        {
            RoleId = Guid.NewGuid(),
            Name = name,
            NameNormalized = name,
            Permissions = permissions,
            UnknownPermissions = unknown.ToHashSet(StringComparer.Ordinal),
            Position = 5,
            Kind = RoleKind.Custom,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Permissions_this_build_does_not_know_survive_every_save(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var roles = driver.CreateRoleStore(options);
        var role = Role(Permission.SendMessages | Permission.ManageRoles, "future_power");

        await roles.InsertAsync(role, default);
        var read = (await roles.FindByIdAsync(role.RoleId, default))!;

        Assert.Equal(Permission.SendMessages | Permission.ManageRoles, read.Permissions);
        Assert.Equal(["future_power"], read.UnknownPermissions);

        // Saving with a new known set — as an edit from this build does — keeps the unknown name.
        await roles.UpdateAsync(read with { Permissions = Permission.ViewChannel }, default);
        var updated = (await roles.FindByIdAsync(role.RoleId, default))!;

        Assert.Equal(Permission.ViewChannel, updated.Permissions);
        Assert.Equal(["future_power"], updated.UnknownPermissions);

        // Lists read the names too, not only single lookups.
        var listed = (await roles.ListAsync(default)).Single(r => r.RoleId == role.RoleId);
        Assert.Equal(Permission.ViewChannel, listed.Permissions);
        Assert.Equal(["future_power"], listed.UnknownPermissions);

        // The old flags column still carries the known bits, for a server downgraded to an older build.
        Assert.Equal((long)Permission.ViewChannel,
            await ScalarAsync(provider, options, $"SELECT permissions FROM raindrop_roles WHERE role_id = '{role.RoleId}'"));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task A_role_with_no_name_rows_is_read_from_the_old_flags_column(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var roles = driver.CreateRoleStore(options);
        var role = Role(Permission.None);
        await roles.InsertAsync(role, default);

        // What a build from before names were stored leaves behind: the flags, and no name rows.
        await ExecuteAsync(provider, options,
            $"UPDATE raindrop_roles SET permissions = {(long)(Permission.Speak | Permission.Connect)} WHERE role_id = '{role.RoleId}'");

        var read = (await roles.FindByIdAsync(role.RoleId, default))!;
        Assert.Equal(Permission.Speak | Permission.Connect, read.Permissions);
        Assert.Empty(read.UnknownPermissions);

        // The next save writes the names, and the role reads the same.
        await roles.UpdateAsync(read, default);
        Assert.Equal(2L, await ScalarAsync(provider, options,
            $"SELECT COUNT(*) FROM raindrop_role_permissions WHERE role_id = '{role.RoleId}'"));
        Assert.Equal(Permission.Speak | Permission.Connect, (await roles.FindByIdAsync(role.RoleId, default))!.Permissions);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Deleting_a_role_deletes_its_permission_rows(string provider)
    {
        var (driver, options) = await TestDatabases.OpenAsync(provider);
        var roles = driver.CreateRoleStore(options);
        var role = Role(Permission.SendMessages, "future_power");
        await roles.InsertAsync(role, default);

        await roles.DeleteAsync(role.RoleId, default);

        Assert.Null(await roles.FindByIdAsync(role.RoleId, default));
        Assert.Equal(0L, await ScalarAsync(provider, options,
            $"SELECT COUNT(*) FROM raindrop_role_permissions WHERE role_id = '{role.RoleId}'"));
    }

    // Raw SQL, for what only the database can show: the old column, and rows the store would not leave.

    private static DbConnection Connect(string provider, DatabaseOptions options) => provider switch
    {
        "sqlite" => new SqliteConnection(options.ConnectionString),
        "postgres" => new NpgsqlConnection(options.ConnectionString),
        _ => new SqlConnection(options.ConnectionString)
    };

    private static async Task<long> ScalarAsync(string provider, DatabaseOptions options, string sql)
    {
        await using var connection = Connect(provider, options);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(string provider, DatabaseOptions options, string sql)
    {
        await using var connection = Connect(provider, options);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Every rule <see cref="RoleService"/> holds: who may change what, the built-in roles, the last owner,
/// and the console's twins that skip the permission check but none of the rest.
/// </summary>
public class RoleServiceTests
{
    private sealed record Setup(
        RoleService Roles, IRoleStore Store, IUserStore Users, RolePermissionResolver Resolver, RecordingPublisher Events);

    private static async Task<Setup> CreateAsync()
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var users = driver.CreateUserStore(options);
        var store = driver.CreateRoleStore(options);
        var roleOptions = new RoleOptions();
        await RoleService.EnsureBuiltInRolesAsync(store, roleOptions, null, default);

        var events = new RecordingPublisher();
        var cache = new RolePermissionCache();
        var resolver = new RolePermissionResolver(store, users, cache);
        return new Setup(new RoleService(store, users, resolver, cache, roleOptions, events), store, users, resolver, events);
    }

    private static async Task<UserSession> MemberAsync(Setup setup) =>
        Sessions.For((await Sessions.ActiveUserAsync(setup.Users)).UserId);

    private static async Task<UserSession> OwnerAsync(Setup setup)
    {
        var owner = await MemberAsync(setup);
        var role = (await setup.Store.FindByKindAsync(RoleKind.Owner, default))!;
        await setup.Roles.AssignFromConsoleAsync(new MemberRoleRequest(owner.UserId, role.RoleId), default);
        return owner;
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ApiException>(act)).Code;

    [Fact]
    public async Task A_member_may_do_the_union_of_every_role_they_hold()
    {
        var setup = await CreateAsync();
        var owner = await OwnerAsync(setup);
        var member = await MemberAsync(setup);

        var moderators = await setup.Roles.CreateAsync(owner, new CreateRoleRequest("Moderators", ["manage_messages"]), default);
        var builders = await setup.Roles.CreateAsync(owner, new CreateRoleRequest("Builders", ["rename_channels", "manage_messages"]), default);
        await setup.Roles.AssignAsync(owner, new MemberRoleRequest(member.UserId, moderators.RoleId), default);
        await setup.Roles.AssignAsync(owner, new MemberRoleRequest(member.UserId, builders.RoleId), default);

        var granted = await setup.Resolver.GetAsync(member.UserId, null, default);

        Assert.True(granted.Allows(Permission.ManageMessages | Permission.RenameChannels | Permission.ViewChannel));
        Assert.False(granted.Allows(Permission.DeleteChannels));
    }

    [Fact]
    public async Task Creating_a_channel_grants_its_creator_nothing_their_roles_do_not()
    {
        var setup = await CreateAsync();
        var member = await MemberAsync(setup);
        var channel = new ChannelRecord
        {
            ChannelId = Guid.NewGuid(),
            Type = ChannelType.Text,
            Name = "mine",
            NameNormalized = "mine",
            CreatedBy = member.UserId,
            CreatedAtUtc = DateTime.UtcNow
        };

        var granted = await setup.Resolver.GetAsync(member.UserId, channel, default);

        Assert.False(granted.Allows(Permission.RenameChannels));
        Assert.False(granted.Allows(Permission.DeleteChannels));
        Assert.False(granted.Allows(Permission.ManageMessages));
    }

    [Fact]
    public async Task Nobody_hands_out_what_they_do_not_hold()
    {
        var setup = await CreateAsync();
        var owner = await OwnerAsync(setup);
        var manager = await MemberAsync(setup);
        var member = await MemberAsync(setup);

        var managers = await setup.Roles.CreateAsync(owner, new CreateRoleRequest("Managers", ["manage_roles", "manage_messages"]), default);
        var admins = await setup.Roles.CreateAsync(owner, new CreateRoleRequest("Admins", ["administrator"]), default);
        await setup.Roles.AssignAsync(owner, new MemberRoleRequest(manager.UserId, managers.RoleId), default);

        Assert.Equal("missing_permission", await CodeOf(() =>
            setup.Roles.CreateAsync(manager, new CreateRoleRequest("Deleters", ["delete_channels"]), default)));
        Assert.Equal("missing_permission", await CodeOf(() =>
            setup.Roles.AssignAsync(manager, new MemberRoleRequest(member.UserId, admins.RoleId), default)));
        Assert.Equal("missing_permission", await CodeOf(() =>
            setup.Roles.UpdateAsync(manager, new UpdateRoleRequest(admins.RoleId, Name: "Renamed"), default)));
        Assert.Equal("missing_permission", await CodeOf(() =>
            setup.Roles.CreateAsync(member, new CreateRoleRequest("Anything"), default)));

        // What they do hold, they may delegate.
        var helpers = await setup.Roles.CreateAsync(manager, new CreateRoleRequest("Helpers", ["manage_messages"]), default);
        var assigned = await setup.Roles.AssignAsync(manager, new MemberRoleRequest(member.UserId, helpers.RoleId), default);
        Assert.True(assigned.Changed);
    }

    [Fact]
    public async Task The_built_in_roles_are_protected_from_the_console_too()
    {
        var setup = await CreateAsync();
        var owner = (await setup.Store.FindByKindAsync(RoleKind.Owner, default))!;
        var everyone = (await setup.Store.FindByKindAsync(RoleKind.Everyone, default))!;

        Assert.Equal("role_immutable", await CodeOf(() =>
            setup.Roles.UpdateFromConsoleAsync(new UpdateRoleRequest(owner.RoleId, Permissions: []), default)));
        Assert.Equal("role_immutable", await CodeOf(() =>
            setup.Roles.UpdateFromConsoleAsync(new UpdateRoleRequest(everyone.RoleId, Name: "all"), default)));
        Assert.Equal("role_immutable", await CodeOf(() =>
            setup.Roles.DeleteFromConsoleAsync(new RoleRequest(everyone.RoleId), default)));

        // Its permissions are what an owner changes, from the console as from a client.
        var edited = await setup.Roles.UpdateFromConsoleAsync(new UpdateRoleRequest(everyone.RoleId, Permissions: ["view_channel"]), default);
        Assert.Equal(["view_channel"], edited.Permissions);
    }

    [Fact]
    public async Task The_console_skips_the_permission_check_but_not_the_last_owner_guard()
    {
        var setup = await CreateAsync();
        var owner = await OwnerAsync(setup);
        var ownerRole = (await setup.Store.FindByKindAsync(RoleKind.Owner, default))!;

        var created = await setup.Roles.CreateFromConsoleAsync(new CreateRoleRequest("Admins", ["administrator"]), default);
        Assert.Contains(setup.Events.Published, e => e.Type == "role.created");

        Assert.Equal("last_owner", await CodeOf(() =>
            setup.Roles.UnassignFromConsoleAsync(new MemberRoleRequest(owner.UserId, ownerRole.RoleId), default)));

        var deleted = await setup.Roles.DeleteFromConsoleAsync(new RoleRequest(created.RoleId), default);
        Assert.Equal(created.RoleId, deleted.RoleId);
        Assert.Null(await setup.Store.FindByIdAsync(created.RoleId, default));
    }

    [Fact]
    public async Task Editing_a_role_keeps_permissions_this_build_does_not_know_and_never_sends_them()
    {
        var setup = await CreateAsync();
        var owner = await OwnerAsync(setup);
        var role = new RoleRecord
        {
            RoleId = Guid.NewGuid(),
            Name = "Newer",
            NameNormalized = "newer",
            Permissions = Permission.SendMessages,
            UnknownPermissions = new HashSet<string>(StringComparer.Ordinal) { "future_power" },
            Position = 3,
            Kind = RoleKind.Custom,
            CreatedAtUtc = DateTime.UtcNow
        };
        await setup.Store.InsertAsync(role, default);

        var edited = await setup.Roles.UpdateAsync(owner, new UpdateRoleRequest(role.RoleId, Permissions: ["view_channel"]), default);

        Assert.Equal(["view_channel"], edited.Permissions);
        Assert.Equal(["future_power"], (await setup.Store.FindByIdAsync(role.RoleId, default))!.UnknownPermissions);
        Assert.Equal("invalid_permission", await CodeOf(() =>
            setup.Roles.UpdateAsync(owner, new UpdateRoleRequest(role.RoleId, Permissions: ["future_power"]), default)));
    }

    [Fact]
    public void Every_permission_has_a_title_and_a_known_category()
    {
        var categories = new[] { "general", "text", "voice", "channels", "moderation", "administration" };

        foreach (var permission in RoleService.Catalogue().Permissions)
        {
            Assert.False(string.IsNullOrWhiteSpace(permission.Title), permission.Name);
            Assert.Contains(permission.Category, categories);
        }
    }
}

/// <summary>Roles from the owner's dashboard.</summary>
public class DashboardRoleTests
{
    private static async Task<DashboardServer.Browser> SignedInAsync(DashboardServer server)
    {
        var browser = server.OnDashboardPort();
        var setup = await browser.PostAsync("/api/setup",
            new { code = server.SetupCode, username = "owner", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        return browser;
    }

    [Fact]
    public async Task Role_endpoints_need_a_signed_in_owner_and_the_dashboards_own_origin()
    {
        await using var server = await DashboardServer.StartAsync();
        var browser = server.OnDashboardPort();

        foreach (var path in new[] { "/api/roles", "/api/permissions" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(path)).StatusCode);

        browser = await SignedInAsync(server);
        browser.Origin = "https://evil.example";
        var forged = await browser.PostAsync("/api/roles", new { name = "Evil", permissions = new[] { "administrator" } });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
    }

    [Fact]
    public async Task The_owner_creates_edits_assigns_and_deletes_a_role()
    {
        await using var server = await DashboardServer.StartAsync();
        await server.RegisterAsync(); // the first member owns the server
        var (_, memberId, username) = await server.RegisterAsync();
        var browser = await SignedInAsync(server);

        var permissions = await browser.JsonAsync("/api/permissions");
        Assert.Contains(permissions.EnumerateArray(), p => p.GetProperty("name").GetString() == "manage_roles"
                                                        && p.GetProperty("title").GetString() == "Manage roles");

        var created = await browser.PostAsync("/api/roles", new { name = "Moderators", permissions = new[] { "manage_messages" } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var roleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("roleId").GetString();

        var taken = await browser.PostAsync("/api/roles", new { name = "moderators" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        var patched = await browser.SendAsync(HttpMethod.Patch, $"/api/roles/{roleId}",
            new { permissions = new[] { "manage_messages", "mute_members" } });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        var assigned = await browser.PostAsync($"/api/accounts/{memberId}/roles/{roleId}");
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var detail = await browser.JsonAsync($"/api/roles/{roleId}");
        Assert.Equal(["manage_messages", "mute_members"],
            detail.GetProperty("role").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal(username, detail.GetProperty("members").EnumerateArray().Single().GetProperty("username").GetString());

        var everyone = (await browser.JsonAsync("/api/roles")).EnumerateArray()
            .Single(r => r.GetProperty("role").GetProperty("kind").GetString() == "everyone");
        var deleteEveryone = await browser.SendAsync(HttpMethod.Delete,
            $"/api/roles/{everyone.GetProperty("role").GetProperty("roleId").GetString()}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteEveryone.StatusCode);

        var deleted = await browser.SendAsync(HttpMethod.Delete, $"/api/roles/{roleId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/api/roles/{roleId}")).StatusCode);
    }
}
