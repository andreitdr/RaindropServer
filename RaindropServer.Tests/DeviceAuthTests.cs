using System.Buffers.Binary;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using RaindropServer.E2ee;
using RaindropServer.Security;
using RaindropServer.Users;

namespace RaindropServer.Tests;

/// <summary>
/// A person's identity key and one of their devices, built the way the Rust core builds them, so a
/// test can sign what a real client would.
/// </summary>
public sealed class TestIdentity
{
    private readonly Ed25519PrivateKeyParameters _identity = new(new SecureRandom());

    public TestIdentity(Guid? userId = null) => UserId = userId ?? Guid.NewGuid();

    public Guid UserId { get; }

    public TestDevice NewDevice() => new(this, new Ed25519PrivateKeyParameters(new SecureRandom()));

    internal byte[] Certify(Guid deviceId, byte[] deviceKey)
    {
        var identityKey = _identity.GeneratePublicKey().GetEncoded();
        var body = new List<byte>();
        Field(body, Encoding.UTF8.GetBytes(UserId.ToString("D")));
        Field(body, Encoding.UTF8.GetBytes(deviceId.ToString("D")));
        Field(body, deviceKey);
        Field(body, identityKey);
        var created = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(created, 1_700_000_000);
        body.AddRange(created);

        var signature = TestDevice.Sign(_identity, [.. "raindrop/device-certificate/v1\n"u8, 1, .. body]);
        Field(body, signature);
        return [1, .. body];
    }

    private static void Field(List<byte> output, byte[] field)
    {
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)field.Length);
        output.AddRange(length);
        output.AddRange(field);
    }
}

public sealed class TestDevice
{
    private readonly Ed25519PrivateKeyParameters _key;

    internal TestDevice(TestIdentity owner, Ed25519PrivateKeyParameters key)
    {
        _key = key;
        Owner = owner;
        Certificate = owner.Certify(DeviceId, key.GeneratePublicKey().GetEncoded());
    }

    public TestIdentity Owner { get; }
    public Guid DeviceId { get; } = Guid.NewGuid();
    public byte[] Certificate { get; }

    public byte[] SignChallenge(byte[] serverKey, byte[] nonce) =>
        Sign(_key, DeviceAuthService.SignedBytes(serverKey, nonce));

    internal static byte[] Sign(Ed25519PrivateKeyParameters key, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }
}

/// <summary>A clock a test moves by hand.</summary>
public sealed class AuthClock : TimeProvider
{
    public DateTime Now { get; set; } = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

public class DeviceAuthTests
{
    private static readonly byte[] ServerKey = [0x04, .. Enumerable.Repeat((byte)7, 64)];

    private sealed record Setup(
        DeviceAuthService Auth, DeviceService Devices, IUserStore Users, InMemorySessionStore Sessions,
        AccountSecurityService Security, AuthClock Clock)
    {
        public UserSession Live(SessionResponse session) =>
            Sessions.TryValidate(session.Token, out var live) ? live : throw new InvalidOperationException("no session");

        public async Task<SessionResponse> SignInAsync(
            TestDevice device, string? username = null, bool withCertificate = true, byte[]? serverKey = null,
            string? code = null)
        {
            var challenge = await Auth.BeginAsync(device.Owner.UserId, default);
            return await Auth.CompleteAsync(
                new CompleteAuthRequest(
                    device.Owner.UserId, device.DeviceId, challenge.Nonce,
                    device.SignChallenge(serverKey ?? ServerKey, challenge.Nonce),
                    withCertificate ? device.Certificate : null, username, code),
                default);
        }
    }

    private static async Task<Setup> CreateAsync()
    {
        var (driver, options) = await TestDatabases.OpenAsync("sqlite");
        var users = driver.CreateUserStore(options);
        var userOptions = new UserOptions();
        var sessions = new InMemorySessionStore(userOptions);
        var e2ee = new E2eeOptions();
        var events = new RecordingPublisher();
        var deviceStore = driver.CreateDeviceStore(options);

        var clock = new AuthClock();
        var security = new AccountSecurityService(
            driver.CreateIdentityVaultStore(options), driver.CreateAuthenticatorStore(options), users,
            new InMemoryPairingRelay(e2ee, clock), e2ee, clock);
        var devices = new DeviceService(
            deviceStore, driver.CreateKeyBackupStore(options), users, new Pbkdf2PasswordHasher(1_000), sessions,
            new InMemoryDeviceLinkRelay(e2ee), events, e2ee, deviceListeners: [security]);
        var accounts = new UserService(users, new Pbkdf2PasswordHasher(1_000), sessions, userOptions, events);
        var identity = new ServerIdentity(ServerIdentity.CurrentProtocolVersion, null, ServerKey);

        return new Setup(
            new DeviceAuthService(users, deviceStore, accounts, devices, security, identity, e2ee, clock),
            devices, users, sessions, security, clock);
    }

    private static string Name() => $"u{Guid.NewGuid():N}"[..20];

    private static async Task<string> RefusalAsync(Func<Task> call) =>
        (await Assert.ThrowsAsync<ApiException>(call)).Code;

    [Fact]
    public async Task A_first_device_registers_the_account_and_gets_a_session_bound_to_it()
    {
        var setup = await CreateAsync();
        var phone = new TestIdentity().NewDevice();

        var session = await setup.SignInAsync(phone, Name());

        Assert.True(setup.Sessions.TryValidate(session.Token, out var live));
        Assert.Equal(phone.Owner.UserId, live.UserId);
        Assert.Equal(phone.DeviceId, live.DeviceId);

        var account = await setup.Users.FindByIdAsync(phone.Owner.UserId, default);
        Assert.Equal(UserStatus.Active, account!.Status);
        Assert.Null(account.PasswordHash);
    }

    [Fact]
    public async Task A_registered_device_signs_in_again_with_its_signature_alone()
    {
        var setup = await CreateAsync();
        var phone = new TestIdentity().NewDevice();
        await setup.SignInAsync(phone, Name());

        var again = await setup.SignInAsync(phone, withCertificate: false);

        Assert.True(setup.Sessions.IsActive(again.Token));
    }

    [Fact]
    public async Task A_second_device_of_the_same_identity_joins_the_account_by_its_certificate()
    {
        var setup = await CreateAsync();
        var person = new TestIdentity();
        var first = await setup.SignInAsync(person.NewDevice(), Name());

        var laptop = person.NewDevice();
        var second = await setup.SignInAsync(laptop);

        // Both stay signed in: a new device does not push the old one out.
        Assert.True(setup.Sessions.IsActive(first.Token));
        Assert.True(setup.Sessions.TryValidate(second.Token, out var live));
        Assert.Equal(laptop.DeviceId, live.DeviceId);
    }

    [Fact]
    public async Task Somebody_elses_identity_cannot_add_a_device_to_the_account()
    {
        var setup = await CreateAsync();
        var owner = new TestIdentity();
        await setup.SignInAsync(owner.NewDevice(), Name());

        // A certificate naming the owner's UUID but signed by another identity key.
        var impostor = new TestIdentity(owner.UserId);

        Assert.Equal("identity_mismatch", await RefusalAsync(() => setup.SignInAsync(impostor.NewDevice())));
    }

    [Fact]
    public async Task A_challenge_is_good_once_for_one_user_and_one_server()
    {
        var setup = await CreateAsync();
        var phone = new TestIdentity().NewDevice();
        await setup.SignInAsync(phone, Name());

        var challenge = await setup.Auth.BeginAsync(phone.Owner.UserId, default);
        var request = new CompleteAuthRequest(
            phone.Owner.UserId, phone.DeviceId, challenge.Nonce,
            phone.SignChallenge(ServerKey, challenge.Nonce), null, null);

        await setup.Auth.CompleteAsync(request, default);
        Assert.Equal("invalid_challenge", await RefusalAsync(() => setup.Auth.CompleteAsync(request, default)));

        // A signature made for another server's key is of no use here.
        byte[] otherServer = [0x04, .. Enumerable.Repeat((byte)9, 64)];
        Assert.Equal("invalid_credentials",
            await RefusalAsync(() => setup.SignInAsync(phone, withCertificate: false, serverKey: otherServer)));

        // Nor is a nonce issued for somebody else.
        var stranger = await setup.Auth.BeginAsync(Guid.NewGuid(), default);
        Assert.Equal("invalid_challenge", await RefusalAsync(() => setup.Auth.CompleteAsync(
            request with { Nonce = stranger.Nonce, Signature = phone.SignChallenge(ServerKey, stranger.Nonce) }, default)));
    }

    [Fact]
    public async Task An_unknown_device_without_a_certificate_and_an_unknown_account_are_refused_alike()
    {
        var setup = await CreateAsync();
        var person = new TestIdentity();
        await setup.SignInAsync(person.NewDevice(), Name());

        Assert.Equal("invalid_credentials",
            await RefusalAsync(() => setup.SignInAsync(person.NewDevice(), withCertificate: false)));
        Assert.Equal("invalid_credentials",
            await RefusalAsync(() => setup.SignInAsync(new TestIdentity().NewDevice(), withCertificate: false)));
    }

    [Fact]
    public async Task A_revoked_device_cannot_sign_in_even_with_its_certificate()
    {
        var setup = await CreateAsync();
        var person = new TestIdentity();
        var phone = person.NewDevice();
        var laptop = person.NewDevice();
        await setup.SignInAsync(phone, Name());
        var laptopSession = await setup.SignInAsync(laptop);

        setup.Sessions.TryValidate(laptopSession.Token, out var bound);
        await setup.Devices.RevokeDeviceAsync(bound, new RevokeDeviceRequest(phone.DeviceId), default);

        Assert.Equal("device_exists", await RefusalAsync(() => setup.SignInAsync(phone)));
        Assert.Equal("invalid_credentials", await RefusalAsync(() => setup.SignInAsync(phone, withCertificate: false)));
    }

    [Fact]
    public async Task A_password_account_that_never_had_a_device_cannot_be_claimed_by_a_certificate()
    {
        var setup = await CreateAsync();
        var attacker = new TestIdentity();
        await Sessions.ActiveUserAsync(setup.Users, attacker.UserId);

        Assert.Equal("invalid_credentials", await RefusalAsync(() => setup.SignInAsync(attacker.NewDevice())));
    }

    [Fact]
    public async Task Registering_needs_a_free_username_and_leaves_nothing_behind_when_refused()
    {
        var setup = await CreateAsync();
        var taken = Name();
        await setup.SignInAsync(new TestIdentity().NewDevice(), taken);

        var late = new TestIdentity().NewDevice();
        Assert.Equal("username_taken", await RefusalAsync(() => setup.SignInAsync(late, taken)));
        Assert.Null(await setup.Users.FindByIdAsync(late.Owner.UserId, default));

        await setup.SignInAsync(late, Name());
    }

    [Fact]
    public async Task A_device_made_by_the_rust_core_signs_in()
    {
        // certificate_len: u16 | certificate | nonce: 32 | signature: 64, written by the Rust core's
        // `print_auth_fixture` for this test's server key. This is the one check that the two
        // implementations of the signed bytes agree.
        var fixture = Fixtures.Load("auth_signature");
        var length = BinaryPrimitives.ReadUInt16BigEndian(fixture);
        var certificateBytes = fixture[2..(2 + length)];
        var nonce = fixture[(2 + length)..(2 + length + 32)];
        var signature = fixture[(2 + length + 32)..];

        var certificate = DeviceCertificate.Parse(certificateBytes);
        Assert.True(certificate.IsSignatureValid());
        Assert.True(Ed25519Signatures.Verify(
            certificate.DeviceSignatureKey, DeviceAuthService.SignedBytes(ServerKey, nonce), signature));

        // And the account id the core derived is one the server takes as a UUID.
        Assert.NotEqual(Guid.Empty, certificate.UserId);
        Assert.Equal(4, certificate.UserId.Version);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_passwordless_account_leaves_from_a_bound_session_and_only_from_one()
    {
        var setup = await CreateAsync();
        var person = new TestIdentity();
        var phone = setup.Live(await setup.SignInAsync(person.NewDevice(), Name()));
        var accounts = new UserService(
            setup.Users, new Pbkdf2PasswordHasher(1_000), setup.Sessions, new UserOptions(), new RecordingPublisher());

        Assert.Equal("device_required", await RefusalAsync(() =>
            accounts.LeaveAsync(Sessions.For(person.UserId), new LeaveRequest("", false), default)));

        await accounts.LeaveAsync(phone, new LeaveRequest("", true), default);
        Assert.Equal(UserStatus.LeftDataPreserved, (await setup.Users.FindByIdAsync(person.UserId, default))!.Status);
    }

    // ── The authenticator ────────────────────────────────────────────────────

    private static readonly byte[] Secret = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();

    private static string CodeAt(Setup setup, int stepsFromNow = 0) =>
        Totp.Code(Secret, Totp.StepAt(setup.Clock.Now) + stepsFromNow);

    private static async Task<(Setup Setup, TestIdentity Person, UserSession Phone)> WithAuthenticatorAsync(
        params string[] backupCodes)
    {
        var setup = await CreateAsync();
        var person = new TestIdentity();
        var phone = setup.Live(await setup.SignInAsync(person.NewDevice(), Name()));
        await setup.Security.EnrollAuthenticatorAsync(
            phone, new EnrollAuthenticatorRequest(Secret, CodeAt(setup), backupCodes), default);
        setup.Clock.Now += TimeSpan.FromMinutes(2);
        return (setup, person, phone);
    }

    [Fact]
    public void Totp_matches_the_rfc_6238_sha1_vectors()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        // RFC 6238 appendix B lists eight digits; an authenticator shows the last six.
        Assert.Equal("287082", Totp.Code(secret, 59 / 30));
        Assert.Equal("081804", Totp.Code(secret, 1111111109 / 30));
        Assert.Equal("005924", Totp.Code(secret, 1234567890 / 30));
    }

    [Fact]
    public async Task A_new_device_needs_a_code_once_the_account_has_an_authenticator()
    {
        var (setup, person, _) = await WithAuthenticatorAsync();
        var laptop = person.NewDevice();

        Assert.Equal("authenticator_required", await RefusalAsync(() => setup.SignInAsync(laptop)));
        Assert.Equal("invalid_authenticator_code", await RefusalAsync(() => setup.SignInAsync(laptop, code: "000000")));

        await setup.SignInAsync(laptop, code: CodeAt(setup));

        // Registered now: it signs in again with no code, and no certificate.
        await setup.SignInAsync(laptop, withCertificate: false);
    }

    [Fact]
    public async Task A_code_adds_one_device_and_no_more()
    {
        var (setup, person, _) = await WithAuthenticatorAsync();
        var code = CodeAt(setup);

        await setup.SignInAsync(person.NewDevice(), code: code);
        Assert.Equal("invalid_authenticator_code", await RefusalAsync(() => setup.SignInAsync(person.NewDevice(), code: code)));

        // The code that proved the secret at enrolment was spent by it, too.
        var (fresh, owner, _) = await WithAuthenticatorAsync();
        fresh.Clock.Now -= TimeSpan.FromMinutes(2);
        Assert.Equal("invalid_authenticator_code",
            await RefusalAsync(() => fresh.SignInAsync(owner.NewDevice(), code: CodeAt(fresh))));
    }

    [Fact]
    public async Task A_backup_code_stands_in_for_the_authenticator_once()
    {
        var (setup, person, phone) = await WithAuthenticatorAsync("ABCD-EFGH-2345", "ZZZZ-YYYY-9876");

        await setup.SignInAsync(person.NewDevice(), code: "abcd efgh 2345");
        Assert.Equal("invalid_authenticator_code",
            await RefusalAsync(() => setup.SignInAsync(person.NewDevice(), code: "ABCD-EFGH-2345")));

        Assert.Equal(new AuthenticatorStatusResponse(true, 1),
            await setup.Security.GetAuthenticatorStatusAsync(phone, default));
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account_out_of_adding_devices_for_a_while()
    {
        var (setup, person, _) = await WithAuthenticatorAsync();
        var laptop = person.NewDevice();

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal("invalid_authenticator_code", await RefusalAsync(() => setup.SignInAsync(laptop, code: "000000")));

        // Even the right code is refused now, so guessing on cannot pay off.
        Assert.Equal("authenticator_locked", await RefusalAsync(() => setup.SignInAsync(laptop, code: CodeAt(setup))));

        setup.Clock.Now += TimeSpan.FromMinutes(16);
        await setup.SignInAsync(laptop, code: CodeAt(setup));
    }

    [Fact]
    public async Task Only_the_identitys_holder_ever_gets_to_try_a_code()
    {
        var (setup, person, _) = await WithAuthenticatorAsync();

        // Signed by another identity key: refused before the authenticator is so much as mentioned.
        var impostor = new TestIdentity(person.UserId).NewDevice();
        Assert.Equal("identity_mismatch", await RefusalAsync(() => setup.SignInAsync(impostor)));
        Assert.Equal("identity_mismatch", await RefusalAsync(() => setup.SignInAsync(impostor, code: CodeAt(setup))));
    }

    [Fact]
    public async Task A_paired_device_replaces_a_lost_authenticator_without_the_old_one()
    {
        var (setup, person, phone) = await WithAuthenticatorAsync();
        var replacement = Enumerable.Range(50, 20).Select(i => (byte)i).ToArray();
        var newCode = Totp.Code(replacement, Totp.StepAt(setup.Clock.Now));

        // A session that is not bound to a device may not.
        Assert.Equal("device_required", await RefusalAsync(() => setup.Security.EnrollAuthenticatorAsync(
            Sessions.For(person.UserId), new EnrollAuthenticatorRequest(replacement, newCode, []), default)));
        Assert.Equal("invalid_authenticator_code", await RefusalAsync(() => setup.Security.EnrollAuthenticatorAsync(
            phone, new EnrollAuthenticatorRequest(replacement, "000000", []), default)));

        await setup.Security.EnrollAuthenticatorAsync(phone, new EnrollAuthenticatorRequest(replacement, newCode, []), default);
        setup.Clock.Now += TimeSpan.FromMinutes(2);

        Assert.Equal("invalid_authenticator_code",
            await RefusalAsync(() => setup.SignInAsync(person.NewDevice(), code: CodeAt(setup))));
        await setup.SignInAsync(person.NewDevice(), code: Totp.Code(replacement, Totp.StepAt(setup.Clock.Now)));

        await setup.Security.DisableAuthenticatorAsync(phone, default);
        await setup.SignInAsync(person.NewDevice());
    }

    // ── The vault ────────────────────────────────────────────────────────────

    private const string LookupId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task The_vault_is_the_owners_and_the_sealed_root_is_found_by_its_lookup_id()
    {
        var setup = await CreateAsync();
        var phone = setup.Live(await setup.SignInAsync(new TestIdentity().NewDevice(), Name()));
        var stranger = setup.Live(await setup.SignInAsync(new TestIdentity().NewDevice(), Name()));

        await setup.Security.PutVaultAsync(phone, new PutIdentityVaultRequest(LookupId, [1, 2, 3], [4, 5]), default);

        var mine = await setup.Security.GetVaultAsync(phone, default);
        Assert.Equal([4, 5], mine.Vault);
        Assert.Null((await setup.Security.GetVaultAsync(stranger, default)).Vault);

        Assert.Equal([1, 2, 3], await setup.Security.GetSealedRootAsync(LookupId, default));
        Assert.Equal("vault_not_found",
            await RefusalAsync(() => setup.Security.GetSealedRootAsync("ffffffffffffffffffffffffffffffff", default)));
        Assert.Equal("vault_not_found", await RefusalAsync(() => setup.Security.GetSealedRootAsync("not-hex", default)));

        // Nobody can park their own blob on somebody else's lookup id, or write without a device.
        Assert.Equal("invalid_vault", await RefusalAsync(() =>
            setup.Security.PutVaultAsync(stranger, new PutIdentityVaultRequest(LookupId, [9], [9]), default)));
        Assert.Equal([1, 2, 3], await setup.Security.GetSealedRootAsync(LookupId, default));
        Assert.Equal("device_required", await RefusalAsync(() => setup.Security.PutVaultAsync(
            Sessions.For(phone.UserId), new PutIdentityVaultRequest(LookupId, [9], [9]), default)));

        // New recovery words move it.
        await setup.Security.PutVaultAsync(
            phone, new PutIdentityVaultRequest("ffffffffffffffffffffffffffffffff", [7], [8]), default);
        Assert.Equal("vault_not_found", await RefusalAsync(() => setup.Security.GetSealedRootAsync(LookupId, default)));
    }

    [Fact]
    public async Task Resetting_an_identity_takes_its_vault_and_authenticator_with_it()
    {
        var (setup, person, phone) = await WithAuthenticatorAsync();
        await setup.Security.PutVaultAsync(phone, new PutIdentityVaultRequest(LookupId, [1], [2]), default);

        await setup.Devices.EraseAsync(person.UserId, default);

        Assert.Equal("vault_not_found", await RefusalAsync(() => setup.Security.GetSealedRootAsync(LookupId, default)));
        Assert.False((await setup.Security.GetAuthenticatorStatusAsync(Sessions.For(person.UserId), default)).Enabled);
    }

    // ── Pairing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_device_with_no_account_is_paired_through_the_relay()
    {
        var setup = await CreateAsync();
        var phone = setup.Live(await setup.SignInAsync(new TestIdentity().NewDevice(), Name()));

        var started = setup.Security.StartPairing([1, 2, 3]);
        Assert.Null(setup.Security.TakePairingApproval(started.PairingId, started.TakeToken));

        // The id is typed by hand, so case and spaces are forgiven.
        var typed = $" {started.PairingId.ToLowerInvariant()} ";
        Assert.Equal([1, 2, 3], await setup.Security.GetPairingOfferAsync(phone, typed, default));
        Assert.Equal("device_required", await RefusalAsync(() =>
            setup.Security.GetPairingOfferAsync(Sessions.For(phone.UserId), typed, default)));

        await setup.Security.ApprovePairingAsync(phone, typed, [9, 9], default);
        Assert.Equal("pairing_not_found", await RefusalAsync(() =>
            setup.Security.ApprovePairingAsync(phone, typed, [8], default)));

        Assert.Null(setup.Security.TakePairingApproval(started.PairingId, "wrong"));
        Assert.Equal([9, 9], setup.Security.TakePairingApproval(started.PairingId, started.TakeToken));
        Assert.Null(setup.Security.TakePairingApproval(started.PairingId, started.TakeToken));
    }

    [Fact]
    public async Task A_pairing_lapses_and_open_pairings_are_bounded()
    {
        var setup = await CreateAsync();
        var started = setup.Security.StartPairing([1]);
        setup.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(setup.Security.TakePairingApproval(started.PairingId, started.TakeToken));

        var relay = new InMemoryPairingRelay(new E2eeOptions { MaximumOpenPairings = 2 });
        Assert.NotNull(relay.Start([1]));
        Assert.NotNull(relay.Start([1]));
        Assert.Null(relay.Start([1]));
    }

    [Fact]
    public void The_throttle_counts_per_caller_and_forgets_after_a_minute()
    {
        var clock = new AuthClock();
        var throttle = new RequestThrottle(new E2eeOptions { AnonymousRequestsPerMinute = 3 }, clock);

        for (var call = 0; call < 3; call++)
            throttle.Check("10.0.0.1");
        Assert.Equal("rate_limited", Assert.Throws<ApiException>(() => throttle.Check("10.0.0.1")).Code);
        throttle.Check("10.0.0.2");

        clock.Now += TimeSpan.FromSeconds(61);
        throttle.Check("10.0.0.1");
    }
}
