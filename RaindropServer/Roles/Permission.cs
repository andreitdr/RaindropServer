namespace RaindropServer.Roles;

/// <summary>
/// One thing a member is allowed to do on this server. Roles carry sets of these, and every check in
/// the server goes through an <see cref="IPermissionResolver"/> that turns a member's roles into the
/// set they actually hold.
/// </summary>
/// <remarks>
/// The backing type is <c>long</c> rather than the default <c>int</c> deliberately: the set is stored
/// in a single database column and there is no schema migration mechanism here, so the room for
/// another thirty-odd permissions has to be there from the start.
/// </remarks>
[Flags]
public enum Permission : long
{
    /// <summary>Nothing at all: not a member of this server, as far as permissions are concerned.</summary>
    None = 0,

    /// <summary>See that a channel exists and read what is in it.</summary>
    ViewChannel = 1L << 0,

    /// <summary>Post in a channel.</summary>
    SendMessages = 1L << 1,

    /// <summary>
    /// Delete other members' messages — moderation. Deleting your own message never needs this: an
    /// author may always delete what they wrote.
    /// </summary>
    ManageMessages = 1L << 2,

    /// <summary>Create new channels on this server.</summary>
    CreateChannels = 1L << 3,

    /// <summary>Rename an existing channel.</summary>
    RenameChannels = 1L << 4,

    /// <summary>Delete a channel, and with it everything inside it.</summary>
    DeleteChannels = 1L << 5,

    /// <summary>
    /// Create, edit and delete roles, and hand them out to members. The permission that governs
    /// every other one, which is why granting it is itself governed — see <see cref="RoleService"/>.
    /// </summary>
    ManageRoles = 1L << 6,

    /// <summary>
    /// Everything, including permissions added to this server after the role was written. The escape
    /// hatch that keeps the built-in owner role correct as the enum grows.
    /// </summary>
    Administrator = 1L << 7,

    /// <summary>Join a voice channel's call and hear the people in it.</summary>
    Connect = 1L << 8,

    /// <summary>
    /// Talk in a voice channel's call. Without it a member who can <see cref="Connect"/> joins as a
    /// listener: the media server refuses to accept their microphone.
    /// </summary>
    Speak = 1L << 9,

    /// <summary>
    /// Mute another member in every voice call on this server — for everyone, not just for yourself —
    /// and lift that mute again. Checked server-wide, never per channel: a mute follows its target
    /// into every call, so only somebody trusted with the whole server may set one.
    /// </summary>
    MuteMembers = 1L << 10,

    /// <summary>
    /// Add files to messages. Checked in the channel alongside <see cref="SendMessages"/>, since a
    /// file travels in a message; taking it away leaves a member able to talk but not to upload.
    /// </summary>
    AttachFiles = 1L << 11,

    /// <summary>
    /// Turn their camera on in a voice channel's call. Separate from <see cref="Speak"/> so a member
    /// can be heard without being seen: without it the media server refuses to accept their camera,
    /// the same way it refuses a microphone without <see cref="Speak"/>.
    /// </summary>
    Video = 1L << 12,

    /// <summary>
    /// Share a screen or a window in a voice channel's call. Separate from <see cref="Video"/>
    /// because it is a different thing to allow: an owner can let members be seen without letting
    /// them put a film or somebody else's document in front of the whole call. A window's sound
    /// comes with it only while they may also speak.
    /// </summary>
    ScreenShare = 1L << 13
}

/// <summary>
/// The wire names for <see cref="Permission"/> and the conversions either way. Permissions travel as
/// names rather than as a bitfield, so a client too old to know one simply does not render it, and a
/// server owner reading a request body can see what it asks for.
/// </summary>
public static class PermissionNames
{
    public const string ViewChannel = "view_channel";
    public const string SendMessages = "send_messages";
    public const string ManageMessages = "manage_messages";
    public const string CreateChannels = "create_channels";
    public const string RenameChannels = "rename_channels";
    public const string DeleteChannels = "delete_channels";
    public const string ManageRoles = "manage_roles";
    public const string Administrator = "administrator";
    public const string Connect = "connect";
    public const string Speak = "speak";
    public const string MuteMembers = "mute_members";
    public const string AttachFiles = "attach_files";
    public const string Video = "video";
    public const string ScreenShare = "screen_share";

    /// <summary>The groups a permission editor shows, in the order it shows them. An open set on the wire.</summary>
    public static class Categories
    {
        public const string General = "general";
        public const string Text = "text";
        public const string Voice = "voice";
        public const string Channels = "channels";
        public const string Moderation = "moderation";
        public const string Administration = "administration";
    }

    /// <summary>
    /// Every permission this server knows, in the order a permission editor lists them: what an
    /// ordinary member does, then moderation, then administration. Everything else here is derived
    /// from this one table, so adding a permission means adding a row. The title and category travel
    /// with it, so a client that was built before the permission existed can still show and group it.
    /// </summary>
    private static readonly (Permission Flag, string Name, string Title, string Category, string Summary)[] Catalogue =
    [
        (Permission.ViewChannel, ViewChannel, "View channels", Categories.General, "See channels and read what is in them."),
        (Permission.SendMessages, SendMessages, "Send messages", Categories.Text, "Post messages in channels."),
        (Permission.AttachFiles, AttachFiles, "Attach files", Categories.Text, "Add files to the messages they post."),
        (Permission.Connect, Connect, "Connect", Categories.Voice, "Join voice channels and hear the people in them."),
        (Permission.Speak, Speak, "Speak", Categories.Voice, "Talk in voice channels."),
        (Permission.Video, Video, "Video", Categories.Voice, "Turn their camera on in voice channels."),
        (Permission.ScreenShare, ScreenShare, "Share screen", Categories.Voice, "Share their screen or a window in voice channels."),
        (Permission.CreateChannels, CreateChannels, "Create channels", Categories.Channels, "Create new channels on this server."),
        (Permission.RenameChannels, RenameChannels, "Rename channels", Categories.Channels, "Rename existing channels."),
        (Permission.DeleteChannels, DeleteChannels, "Delete channels", Categories.Channels, "Delete channels, and everything inside them."),
        (Permission.ManageMessages, ManageMessages, "Manage messages", Categories.Moderation, "Delete other members' messages."),
        (Permission.MuteMembers, MuteMembers, "Mute members", Categories.Moderation, "Mute other members in voice channels, for everyone."),
        (Permission.ManageRoles, ManageRoles, "Manage roles", Categories.Administration, "Create roles and hand them out to members."),
        (Permission.Administrator, Administrator, "Administrator", Categories.Administration, "Every permission, including ones added later.")
    ];

    /// <summary>Every permission this server understands, as one set.</summary>
    public static Permission All { get; } =
        Catalogue.Aggregate(Permission.None, (all, entry) => all | entry.Flag);

    /// <summary>The catalogue a client draws a permission editor from, in display order.</summary>
    public static IReadOnlyList<PermissionDescription> Describe { get; } =
        Catalogue.Select(entry => new PermissionDescription(entry.Name, entry.Title, entry.Category, entry.Summary))
                 .ToList();

    /// <summary>The flag a stored or requested name stands for, or null when this build has no such permission.</summary>
    public static Permission? Lookup(string? name)
    {
        var trimmed = name?.Trim().ToLowerInvariant();
        var match = Catalogue.FirstOrDefault(entry => entry.Name == trimmed);

        return match.Name is null ? null : match.Flag;
    }

    /// <summary>
    /// Reads a stored set of names back: the ones this build knows become flags, and the rest are kept
    /// aside untouched. A role saved by a newer build — or a modified one — may name permissions this
    /// build has never heard of; they grant nothing here, and keeping them means saving the role from
    /// this build cannot erase them.
    /// </summary>
    public static (Permission Known, IReadOnlySet<string> Unknown) Split(IEnumerable<string> stored)
    {
        var known = Permission.None;
        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var name in stored)
        {
            if (Lookup(name) is { } flag)
                known |= flag;
            else if (!string.IsNullOrWhiteSpace(name))
                unknown.Add(name);
        }

        return (known, unknown);
    }

    /// <summary>Expands a flag set into the names a client sees, in the catalogue's order.</summary>
    public static IReadOnlyList<string> Of(Permission permissions)
    {
        var names = new List<string>(Catalogue.Length);

        foreach (var (flag, name, _, _, _) in Catalogue)
        {
            if (permissions.HasFlag(flag))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Reads a set of permission names sent by a client. A name this server has never heard of is
    /// refused rather than ignored — silently dropping it would leave the caller believing they had
    /// granted something they had not.
    /// </summary>
    /// <exception cref="ApiException">One of the names is not a permission.</exception>
    public static Permission Parse(IEnumerable<string>? names)
    {
        if (names is null)
            return Permission.None;

        var permissions = Permission.None;

        foreach (var name in names)
        {
            permissions |= Lookup(name) ?? throw ApiException.BadRequest("invalid_permission",
                $"'{name}' is not a permission on this server. RoleService.ListPermissions has the list.");
        }

        return permissions;
    }

    /// <summary>
    /// Reads the old flags column, dropping bits this build does not know. Only used to copy a role
    /// written before permissions were stored by name; the names are what is read since.
    /// </summary>
    public static Permission FromStorage(long stored) => (Permission)stored & All;
}

/// <param name="Name">The wire name, e.g. <c>manage_roles</c>.</param>
/// <param name="Title">A short label for an editor, e.g. "Manage roles".</param>
/// <param name="Category">
/// The group an editor files it under — one of <see cref="PermissionNames.Categories"/>, an open set.
/// </param>
/// <param name="Summary">One line describing what it allows, safe to show in a permission editor.</param>
public record PermissionDescription(string Name, string Title, string Category, string Summary);

/// <summary>Comparing permission sets, and turning a missing one into the API error it is.</summary>
public static class PermissionExtensions
{
    /// <summary>
    /// Resolves <see cref="Permission.Administrator"/> into the set it stands for. Every comparison
    /// happens on expanded sets, so an administrator is never refused something merely because the
    /// bit for it is not literally set on their role.
    /// </summary>
    public static Permission Expand(this Permission permissions) =>
        permissions.HasFlag(Permission.Administrator) ? PermissionNames.All : permissions;

    /// <summary>Whether every bit in <paramref name="required"/> is granted.</summary>
    public static bool Allows(this Permission granted, Permission required) =>
        (granted.Expand() & required) == required;

    /// <summary>
    /// Throws 403 unless <paramref name="required"/> is granted. <paramref name="action"/> completes
    /// the sentence "You do not have permission to …".
    /// </summary>
    /// <exception cref="ApiException">The permission is not granted.</exception>
    public static void Require(this Permission granted, Permission required, string action)
    {
        if (!granted.Allows(required))
            throw ApiException.Forbidden("missing_permission", $"You do not have permission to {action}.");
    }
}
