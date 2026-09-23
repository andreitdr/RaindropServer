using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RaindropServer.Attachments;
using RaindropServer.Channels;
using RaindropServer.Channels.Text;
using RaindropServer.Channels.Voice;
using RaindropServer.Configuration;
using RaindropServer.Data;
using RaindropServer.E2ee;
using RaindropServer.Events;
using RaindropServer.Events.WebSockets;
using RaindropServer.Members;
using RaindropServer.Payloads;
using RaindropServer.Roles;
using RaindropServer.Users;

namespace RaindropServer;

/// <summary>
/// Extension methods for registering RaindropServer services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core RaindropServer infrastructure — the user subsystem plus automatic discovery
    /// of every endpoint implementation found in all currently-loaded assemblies. Just implement one
    /// of the endpoint interfaces; no manual registration is needed.
    /// </summary>
    /// <param name="services">The container to add to.</param>
    /// <param name="configureUsers">Optionally adjusts account, session and retention rules.</param>
    /// <param name="configureChannels">Optionally adjusts channel and message rules.</param>
    /// <param name="configureRoles">Optionally adjusts what roles a fresh server starts with.</param>
    /// <param name="configureEvents">Optionally adjusts how the event stream treats its connections.</param>
    /// <param name="configureVoice">Optionally adjusts voice call rules. The media server itself is registered separately — see <see cref="AddRaindropVoice"/>.</param>
    /// <param name="configureAttachments">Optionally adjusts which files members may add to messages.</param>
    /// <remarks>
    /// A database module still has to be enabled with <see cref="AddRaindropDatabase(IServiceCollection, Action{DatabaseOptions})"/>
    /// before the server can do anything with users.
    /// </remarks>
    public static IServiceCollection AddRaindropServer(
        this IServiceCollection services,
        Action<UserOptions>? configureUsers = null,
        Action<ChannelOptions>? configureChannels = null,
        Action<RoleOptions>? configureRoles = null,
        Action<EventOptions>? configureEvents = null,
        Action<VoiceOptions>? configureVoice = null,
        Action<AttachmentOptions>? configureAttachments = null)
    {
        services.AddRouting();
        services.AddRaindropPayloads();
        services.AddRaindropEvents(configureEvents);
        services.AddRaindropUsers(configureUsers);
        services.AddRaindropRoles(configureRoles);
        services.AddRaindropMembers();

        // Before channels: text channels fall back to refusing every file unless something that stores
        // them has registered first.
        services.AddRaindropAttachments(configureAttachments);
        services.AddRaindropChannels(configureChannels);
        services.AddRaindropVoice(configureVoice);
        services.AddRaindropE2ee();
        services.AddDiscoveredEndpoints();

        return services;
    }

    /// <summary>
    /// Registers the wire formats the server can speak. JSON is the default so that a request can be
    /// made by hand; a client that wants the bytes asks for MessagePack per request.
    /// </summary>
    public static IServiceCollection AddRaindropPayloads(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPayloadCodec, JsonPayloadCodec>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPayloadCodec, MessagePackPayloadCodec>());
        services.TryAddSingleton<PayloadCodecs>();

        // A placeholder identity so the library stands on its own; the host registers the real one,
        // with its certificate fingerprint, afterwards.
        services.TryAddSingleton(new ServerIdentity(ServerIdentity.CurrentProtocolVersion, null));

        return services;
    }

    /// <summary>
    /// Registers the event system: the publisher every subsystem announces its changes through, the
    /// registry transports hand their connections to, and the WebSocket transport this server ships.
    /// Called for you by <see cref="AddRaindropServer"/>, and by every subsystem that publishes.
    /// </summary>
    /// <remarks>
    /// Each seam is its own registration so a plugin can replace one without the others: register an
    /// <see cref="IEventPublisher"/> first to relay events somewhere shared, an
    /// <see cref="IEventConnectionRegistry"/> to hold connections elsewhere, or add an
    /// <see cref="IServerEventHandler{T}"/> — with <see cref="AddServerEventHandler{T, THandler}"/> —
    /// to react to events in-process. The default for the first two is one <see cref="EventHub"/>.
    /// </remarks>
    public static IServiceCollection AddRaindropEvents(
        this IServiceCollection services, Action<EventOptions>? configure = null)
    {
        // Re-configuring has to reach the instance already registered, or the second call would be
        // silently ignored by the TryAdd below.
        var options = services.FindInstance<EventOptions>() ?? new EventOptions();
        configure?.Invoke(options);

        services.AddRaindropPayloads();
        services.TryAddSingleton(options);

        services.TryAddSingleton<EventHub>();
        services.TryAddSingleton<IEventPublisher>(sp => sp.GetRequiredService<EventHub>());
        services.TryAddSingleton<IEventConnectionRegistry>(sp => sp.GetRequiredService<EventHub>());

        // The hub closes a connection whose session has just been revoked, so it has to hear about it.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ISessionListener, EventHub>(sp => sp.GetRequiredService<EventHub>()));

        // Transports.
        services.TryAddSingleton<WebSocketEventStream>();

        return services;
    }

    /// <summary>
    /// Registers server code to run whenever an event with a <typeparamref name="T"/> payload is
    /// published — the way a plugin reacts to what happens on the server.
    /// </summary>
    public static IServiceCollection AddServerEventHandler<T, THandler>(this IServiceCollection services)
        where THandler : class, IServerEventHandler<T>
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IServerEventHandler<T>, THandler>());
        return services;
    }

    /// <summary>
    /// Registers user accounts, sessions, password hashing and the retention sweep. Called for you by
    /// <see cref="AddRaindropServer"/>; call it directly only to adjust the options afterwards.
    /// </summary>
    public static IServiceCollection AddRaindropUsers(
        this IServiceCollection services, Action<UserOptions>? configure = null)
    {
        // Re-configuring has to reach the instance already registered, or the second call would be
        // silently ignored by the TryAdd below.
        var options = services.FindInstance<UserOptions>() ?? new UserOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddRaindropEvents();
        services.TryAddSingleton<IPasswordHasher>(_ => new Pbkdf2PasswordHasher());
        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddSingleton<UserService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RetentionCleanupService>());

        return services;
    }

    /// <summary>
    /// Registers roles, the permission resolver that reads them, and the rule that hands the first
    /// UUID to register the owner role. Called for you by <see cref="AddRaindropServer"/>; call it
    /// directly only to adjust the options afterwards, or to register a different
    /// <see cref="IPermissionResolver"/> first — one registered before this runs wins, since this
    /// only fills in the default.
    /// </summary>
    /// <remarks>
    /// The built-in roles are not created here. They are rows rather than registrations, so they are
    /// seeded by <see cref="InitializeRaindropDatabaseAsync"/> alongside the schema — before the
    /// server starts listening, so the owner role exists by the time anybody can register.
    /// </remarks>
    public static IServiceCollection AddRaindropRoles(
        this IServiceCollection services, Action<RoleOptions>? configure = null)
    {
        // Re-configuring has to reach the instance already registered, or the second call would be
        // silently ignored by the TryAdd below.
        var options = services.FindInstance<RoleOptions>() ?? new RoleOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddRaindropEvents();
        services.TryAddSingleton<RolePermissionCache>();
        services.TryAddSingleton<IPermissionResolver, RolePermissionResolver>();
        services.TryAddSingleton<RoleService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegistrationListener, OwnerClaim>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILeaveGuard, OwnerLeaveGuard>());

        return services;
    }

    /// <summary>
    /// Registers the roster and the presence behind it. Called for you by
    /// <see cref="AddRaindropServer"/>; call it directly only to swap in a different
    /// <see cref="IPresenceTracker"/> first — registering one before this runs wins, since this
    /// only fills in the default.
    /// </summary>
    public static IServiceCollection AddRaindropMembers(this IServiceCollection services)
    {
        services.TryAddSingleton<IPresenceTracker, SessionPresenceTracker>();
        services.TryAddSingleton<MemberService>();

        // Presence is derived from sessions, so a change to them is what a roster needs to hear about.
        services.AddRaindropEvents();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISessionListener, PresenceBroadcaster>());

        return services;
    }

    /// <summary>
    /// Registers channels and every kind of channel this server can host. Called for you by
    /// <see cref="AddRaindropServer"/>; call it directly only to adjust the options afterwards.
    /// </summary>
    /// <remarks>
    /// Each channel type is registered as an <see cref="IChannelModule"/>, and the shared channel
    /// code will only create a type that has one. Text channels are registered here; voice channels
    /// by <see cref="AddRaindropVoice"/>, which brings the rest of the voice subsystem with it.
    /// <para>
    /// Channels do not own their permission rules: every check goes through the
    /// <see cref="IPermissionResolver"/> the roles subsystem registers, which is why this brings
    /// <see cref="AddRaindropRoles"/> in with it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRaindropChannels(
        this IServiceCollection services, Action<ChannelOptions>? configure = null)
    {
        // Re-configuring has to reach the instance already registered, or the second call would be
        // silently ignored by the TryAdd below.
        var options = services.FindInstance<ChannelOptions>() ?? new ChannelOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddRaindropRoles();
        services.AddRaindropEvents();
        services.TryAddSingleton<ChannelService>();

        // Text channels.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelModule, TextChannelModule>());
        services.TryAddSingleton<TextChannelService>();
        services.TryAddSingleton<MessageReportService>();

        // Deleted messages are copied into the database before their text is destroyed. A plugin adds
        // its own IMessageDeletionListener beside this one; it does not replace it.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageDeletionListener, ArchivingMessageDeletionListener>());

        // Files in messages: refused unless AddRaindropAttachments, or a plugin, registered a store first.
        services.TryAddSingleton<IMessageAttachments, NoMessageAttachments>();

        return services;
    }

    /// <summary>
    /// Registers files in messages: the service that receives uploads and hands them back, the folder
    /// they are kept in, the owner's rules, and the sweep that clears away what nobody can reach. Called
    /// for you by <see cref="AddRaindropServer"/>, before the channels whose messages carry the files.
    /// </summary>
    /// <remarks>
    /// Files are end-to-end encrypted by clients; everything here handles ciphertext. Each seam is
    /// <c>TryAdd</c>, so a plugin registers its own first to replace it: an <see cref="IAttachmentStorage"/>
    /// to keep files somewhere other than a local folder, or an <see cref="IMessageAttachments"/> to take
    /// files over entirely. <see cref="IAttachmentPolicy"/> and <see cref="IAttachmentListener"/>
    /// implementations are added beside the defaults with <c>TryAddEnumerable</c>.
    /// </remarks>
    public static IServiceCollection AddRaindropAttachments(
        this IServiceCollection services, Action<AttachmentOptions>? configure = null)
    {
        var options = services.FindInstance<AttachmentOptions>() ?? new AttachmentOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IAttachmentStorage>(sp =>
            new LocalFolderAttachmentStorage(sp.GetRequiredService<AttachmentOptions>()));
        services.TryAddSingleton<AttachmentService>();
        services.TryAddSingleton<IMessageAttachments>(sp => sp.GetRequiredService<AttachmentService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAttachmentPolicy, ConfiguredAttachmentPolicy>());

        // A message's files go when it does; the maintenance pass removes them from storage afterwards.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageDeletionListener, AttachmentDeletionListener>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AttachmentMaintenanceService>());
        services.AddStartupCheck<AttachmentStorageStartupCheck>();

        services.AddRaindropChannels();

        return services;
    }

    /// <summary>
    /// Registers voice channels and the calls in them. Called for you by <see cref="AddRaindropServer"/>.
    /// </summary>
    /// <remarks>
    /// Voice needs a media server to carry the audio, registered as an <see cref="IVoiceTransport"/>
    /// <em>before</em> this runs — the <c>RaindropServer.Voice.LiveKit</c> assembly's
    /// <c>AddRaindropLiveKit</c> does that. Without one, <see cref="UnavailableVoiceTransport"/> fills
    /// the gap: voice channels can still be created and deleted, but joining one is refused.
    /// <para>
    /// Everything here is <c>TryAdd</c>, so a plugin replaces the state store, the transport or the
    /// background worker by registering its own first; <see cref="IVoiceJoinPolicy"/> implementations
    /// are added beside each other with <c>TryAddEnumerable</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRaindropVoice(
        this IServiceCollection services, Action<VoiceOptions>? configure = null)
    {
        var options = services.FindInstance<VoiceOptions>() ?? new VoiceOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddRaindropChannels();

        services.TryAddSingleton<IVoiceTransport, UnavailableVoiceTransport>();
        services.TryAddSingleton<IVoiceStateStore, InMemoryVoiceStateStore>();
        services.TryAddSingleton<VoiceWorkQueue>();
        services.TryAddSingleton<VoiceService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelModule, VoiceChannelModule>());

        // Keeping calls in step with sessions, permissions and the media server.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISessionListener, VoiceSessionListener>());
        services.AddServerEventHandler<RoleSummary, VoicePermissionWatcher>();
        services.AddServerEventHandler<RoleDeletedEvent, VoicePermissionWatcher>();
        services.AddServerEventHandler<MemberRolesEvent, VoicePermissionWatcher>();
        services.AddServerEventHandler<MemberEvent, VoicePermissionWatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, VoiceMaintenanceService>());

        return services;
    }

    /// <summary>
    /// Registers end-to-end encryption identities: devices and identity keys, session binding, key
    /// backups and the device-link relay. Called for you by <see cref="AddRaindropServer"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IDeviceLinkRelay"/> and <see cref="IE2eeMembershipPolicy"/> are <c>TryAdd</c>, so a
    /// plugin can hold links somewhere shared or decide differently who belongs in a group.
    /// <see cref="IDeviceListener"/> implementations are added beside each other with
    /// <c>TryAddEnumerable</c>.
    /// </remarks>
    public static IServiceCollection AddRaindropE2ee(
        this IServiceCollection services, Action<E2eeOptions>? configure = null)
    {
        var options = services.FindInstance<E2eeOptions>() ?? new E2eeOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.AddRaindropUsers();
        services.TryAddSingleton<IDeviceLinkRelay, InMemoryDeviceLinkRelay>();
        services.TryAddSingleton<DeviceService>();
        services.AddServerEventHandler<MemberEvent, ErasedMemberCleanup>();

        // The MLS delivery service, and keeping its groups in step with who may be in them.
        services.AddRaindropRoles();
        services.TryAddSingleton<IE2eeMembershipPolicy, ChannelPermissionMembershipPolicy>();
        services.TryAddSingleton<MlsDeliveryService>();
        services.TryAddSingleton<GroupMembershipReconciler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDeviceListener, GroupMembershipReconciler>(
            sp => sp.GetRequiredService<GroupMembershipReconciler>()));
        services.AddServerEventHandler<RoleSummary, GroupMembershipReconciler>();
        services.AddServerEventHandler<RoleDeletedEvent, GroupMembershipReconciler>();
        services.AddServerEventHandler<MemberRolesEvent, GroupMembershipReconciler>();
        services.AddServerEventHandler<MemberEvent, GroupMembershipReconciler>();
        services.AddServerEventHandler<ChannelDeletedEvent, GroupMembershipReconciler>();

        return services;
    }

    /// <summary>
    /// Enables one of the installed database modules. Nothing is persisted until this is called, and
    /// the server owner chooses the driver purely by name — "sqlite", "postgres", "sqlserver", or
    /// anything else they have dropped into the <c>drivers/</c> folder.
    /// </summary>
    /// <exception cref="InvalidOperationException">No driver by that name is installed.</exception>
    public static IServiceCollection AddRaindropDatabase(
        this IServiceCollection services, Action<DatabaseOptions> configure)
    {
        var options = new DatabaseOptions();
        configure(options);

        return services.AddRaindropDatabase(options);
    }

    /// <inheritdoc cref="AddRaindropDatabase(IServiceCollection, Action{DatabaseOptions})" />
    public static IServiceCollection AddRaindropDatabase(this IServiceCollection services, DatabaseOptions options)
    {
        options.Validate();

        var driver = DatabaseDriverRegistry.Resolve(options.Provider);

        services.AddSingleton(options);
        services.AddSingleton(driver);
        services.AddSingleton(driver.CreateUserStore(options));
        services.AddSingleton(driver.CreateChannelStore(options));
        services.AddSingleton(driver.CreateRoleStore(options));
        services.AddSingleton(driver.CreateTextMessageStore(options));
        services.AddSingleton(driver.CreateDeletedMessageStore(options));
        services.AddSingleton(driver.CreateVoiceMuteStore(options));
        services.AddSingleton(driver.CreateDeviceStore(options));
        services.AddSingleton(driver.CreateKeyBackupStore(options));
        services.AddSingleton(driver.CreateMlsGroupStore(options));
        services.AddSingleton(driver.CreateKeyPackageStore(options));
        services.AddSingleton(driver.CreateWelcomeStore(options));
        services.AddSingleton(driver.CreateMessageReportStore(options));
        services.AddSingleton(driver.CreateAttachmentStore(options));

        return services;
    }

    /// <summary>
    /// Gets the persistence ready: creates any missing tables and indexes for the enabled database
    /// module, then seeds the two built-in roles if this server has never had them. Call this once,
    /// before <see cref="ServerHost.Start"/>, so both are in place before the first request arrives —
    /// in particular so that the owner role exists by the time the first UUID can register.
    /// </summary>
    /// <exception cref="InvalidOperationException">No database module has been enabled.</exception>
    public static async Task InitializeRaindropDatabaseAsync(
        this IServiceCollection services, CancellationToken cancellationToken = default)
    {
        var options = services.FindInstance<DatabaseOptions>();
        var driver = services.FindInstance<IDatabaseDriver>();

        if (options is null || driver is null)
            throw new InvalidOperationException(
                "No database module is enabled. Call AddRaindropDatabase(...) before starting the server. " +
                $"Installed drivers: {DatabaseDriverRegistry.DescribeAvailable()}.");

        if (options.WipeOnStartup)
        {
            Console.WriteLine("[raindrop] database.wipeOnStartup is set — dropping every table before it starts. " +
                               "Development only; never leave this on where the database holds anything worth keeping.");
            await driver.ResetSchemaAsync(options, cancellationToken);
        }

        if (options.AutoCreateSchema)
            await driver.EnsureSchemaAsync(options, cancellationToken);

        await services.SeedRaindropRolesAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the built-in <c>everyone</c> and <c>owner</c> roles if they are not there yet. Does
    /// nothing when the roles subsystem has not been registered, so a host that only wants users can
    /// still initialize its database.
    /// </summary>
    /// <remarks>
    /// This has to run before <see cref="ServerHost.Start"/> rather than as a hosted service, because
    /// a hosted service has no guaranteed ordering against Kestrel: a request could arrive first, and
    /// the very first request to a fresh server is somebody registering and expecting to own it. It
    /// works on the registered instances directly rather than building a container, so the rows land
    /// in the same database the running server is about to use.
    /// </remarks>
    private static async Task SeedRaindropRolesAsync(
        this IServiceCollection services, CancellationToken cancellationToken)
    {
        var store = services.FindInstance<IRoleStore>();
        var options = services.FindInstance<RoleOptions>();

        if (store is null || options is null)
            return;

        await RoleService.EnsureBuiltInRolesAsync(
            store, options, services.FindInstance<TimeProvider>(), cancellationToken);
    }

    /// <summary>
    /// Manually registers a single <see cref="IEndpoint"/> implementation.
    /// Only needed when auto-discovery cannot find the type (e.g. the assembly hasn't been loaded yet).
    /// </summary>
    public static IServiceCollection AddEndpoint<T>(this IServiceCollection services)
        where T : class, IEndpoint
    {
        services.AddTransient<IEndpoint, T>();
        return services;
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans every loaded assembly for the four endpoint interfaces and registers each implementation
    /// as an <see cref="IEndpoint"/> — wrapping the typed and authenticated ones in the adapter that
    /// gives them JSON handling and session resolution.
    /// </summary>
    private static void AddDiscoveredEndpoints(this IServiceCollection services)
    {
        var endpointType = typeof(IEndpoint);

        var candidates = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .Where(t => !t.IsDefined(typeof(SkipEndpointDiscoveryAttribute), inherit: false))
            .ToList();

        foreach (var type in candidates)
        {
            // Plain endpoints are mapped as they are.
            if (endpointType.IsAssignableFrom(type))
                services.AddTransient(endpointType, type);

            if (typeof(IAuthenticatedEndpoint).IsAssignableFrom(type))
                services.AddAdapted(type, sp => new AuthenticatedEndpointAdapter(
                    (IAuthenticatedEndpoint)sp.GetRequiredService(type),
                    sp.GetRequiredService<ISessionStore>()));

            if (ClosedInterface(type, typeof(ITypedEndpoint<,>)) is { } typed)
            {
                var adapterType = typeof(TypedEndpointAdapter<,>).MakeGenericType(typed.GetGenericArguments());

                services.AddAdapted(type, sp => (IEndpoint)Activator.CreateInstance(
                    adapterType,
                    sp.GetRequiredService(type),
                    sp.GetRequiredService<PayloadCodecs>())!);
            }

            if (ClosedInterface(type, typeof(IAuthenticatedTypedEndpoint<,>)) is { } authenticatedTyped)
            {
                var adapterType = typeof(AuthenticatedTypedEndpointAdapter<,>)
                    .MakeGenericType(authenticatedTyped.GetGenericArguments());

                services.AddAdapted(type, sp => (IEndpoint)Activator.CreateInstance(
                    adapterType,
                    sp.GetRequiredService(type),
                    sp.GetRequiredService<ISessionStore>(),
                    sp.GetRequiredService<PayloadCodecs>())!);
            }
        }
    }

    private static Type? ClosedInterface(Type type, Type openGeneric) =>
        type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric);

    /// <summary>
    /// Registers the endpoint under its own concrete type — so each adapter resolves exactly the
    /// implementation it wraps, even when two endpoints share a request/response pair — and the
    /// adapter around it as an <see cref="IEndpoint"/>.
    /// </summary>
    private static void AddAdapted(
        this IServiceCollection services, Type endpointType, Func<IServiceProvider, IEndpoint> adapter)
    {
        services.TryAddTransient(endpointType);
        services.AddTransient(typeof(IEndpoint), adapter);
    }

    /// <summary>
    /// Registers a check to be run once at startup, before the server listens — the seam a module
    /// uses to confirm that what its settings describe is really there. Run them all with
    /// <see cref="RunRaindropStartupChecksAsync"/>.
    /// </summary>
    public static IServiceCollection AddStartupCheck<T>(this IServiceCollection services)
        where T : class, IStartupCheck
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupCheck, T>());
        return services;
    }

    /// <summary>
    /// Runs every registered <see cref="IStartupCheck"/> and reports what they found, so a server
    /// owner hears about an unreachable media server or a refused credential while the server is
    /// starting rather than from the first member it fails.
    /// </summary>
    /// <remarks>
    /// The checks run against a container of their own, built from these registrations and thrown
    /// away afterwards: nothing a check touches outlives it, and the running server starts with its
    /// own clean instances. A check that throws anyway is recorded as an error rather than being
    /// allowed to take the process down.
    /// </remarks>
    public static async Task<ConfigurationReport> RunRaindropStartupChecksAsync(
        this IServiceCollection services,
        ConfigurationReport? report = null,
        CancellationToken cancellationToken = default)
    {
        report ??= new ConfigurationReport();

        await using var provider = services.BuildServiceProvider();

        foreach (var check in provider.GetServices<IStartupCheck>())
        {
            try
            {
                await check.CheckAsync(report, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Error(check.Name, $"could not be checked: {ex.Message}");
            }
        }

        return report;
    }

    /// <summary>Finds an instance already registered as a singleton, so a later call can reuse or adjust it.</summary>
    private static T? FindInstance<T>(this IServiceCollection services) where T : class =>
        services.LastOrDefault(d => d.ServiceType == typeof(T))?.ImplementationInstance as T;
}
