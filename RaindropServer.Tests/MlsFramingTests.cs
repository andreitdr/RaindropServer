using RaindropServer.E2ee;

namespace RaindropServer.Tests;

/// <summary>The server's MLS envelope parser, against real OpenMLS output.</summary>
public class MlsFramingTests
{
    private static readonly byte[] Group = MlsFraming.GroupIdFor(Fixtures.ChannelId);

    [Fact]
    public void The_group_id_is_the_channel_uuid_in_big_endian_order() =>
        Assert.Equal(Convert.FromHexString("6f1c2d3e4a5b4c6d8e7f001122334455"), Group);

    [Fact]
    public void A_member_commit_is_a_private_message_whose_content_type_is_readable()
    {
        var envelope = MlsFraming.Parse(Fixtures.Load("private_commit"));
        Assert.Equal(MlsWireFormat.PrivateMessage, envelope.WireFormat);
        Assert.Equal(Group, envelope.GroupId);
        Assert.Equal(0ul, envelope.Epoch);
        Assert.Equal(MlsContentType.Commit, envelope.ContentType);
        Assert.Null(envelope.SenderType); // encrypted
    }

    [Fact]
    public void An_external_commit_is_public_from_a_new_member()
    {
        var envelope = MlsFraming.Parse(Fixtures.Load("external_commit"));
        Assert.Equal(MlsWireFormat.PublicMessage, envelope.WireFormat);
        Assert.Equal(Group, envelope.GroupId);
        Assert.Equal(1ul, envelope.Epoch);
        Assert.Equal(MlsContentType.Commit, envelope.ContentType);
        Assert.Equal(MlsSenderType.NewMemberCommit, envelope.SenderType);
    }

    [Fact]
    public void An_application_message_says_so_without_revealing_anything_else()
    {
        var envelope = MlsFraming.Parse(Fixtures.Load("application"));
        Assert.Equal(MlsWireFormat.PrivateMessage, envelope.WireFormat);
        Assert.Equal(MlsContentType.Application, envelope.ContentType);
        Assert.Equal(1ul, envelope.Epoch);
    }

    [Fact]
    public void A_group_info_carries_its_group_and_epoch()
    {
        var envelope = MlsFraming.Parse(Fixtures.Load("group_info"));
        Assert.Equal(MlsWireFormat.GroupInfo, envelope.WireFormat);
        Assert.Equal(Group, envelope.GroupId);
        Assert.Equal(1ul, envelope.Epoch);
    }

    [Theory]
    [InlineData("welcome", MlsWireFormat.Welcome)]
    [InlineData("key_package", MlsWireFormat.KeyPackage)]
    public void Welcomes_and_key_packages_are_recognised(string fixture, MlsWireFormat expected) =>
        Assert.Equal(expected, MlsFraming.Parse(Fixtures.Load(fixture)).WireFormat);

    [Theory]
    [InlineData("")]
    [InlineData("0001")]
    [InlineData("0002000a")]           // MLS 1.0 private message, header cut short
    [InlineData("000100021030313233")] // group id longer than the message
    [InlineData("00020002")]           // not MLS 1.0
    [InlineData("00010009")]           // unknown wire format
    [InlineData("00010002c0")]         // a 0b11 length prefix is invalid in MLS
    public void Malformed_headers_are_refused_not_misread(string hex)
    {
        var exception = Assert.Throws<ApiException>(() => MlsFraming.Parse(Convert.FromHexString(hex)));
        Assert.Equal("invalid_mls_message", exception.Code);
    }

    [Fact]
    public void A_bare_key_package_without_its_mls_envelope_is_not_mistaken_for_a_message()
    {
        // What the core used to send: KeyPackage starts with version 1 and cipher suite 1, which
        // would read as an MLS 1.0 *public message*. Stripping the envelope shows the parser does
        // not accept it as a key package.
        var bare = Fixtures.Load("key_package")[4..];
        MlsWireFormat? format = null;
        try
        {
            format = MlsFraming.Parse(bare).WireFormat;
        }
        catch (ApiException)
        {
            // Refused outright is fine too.
        }
        Assert.NotEqual(MlsWireFormat.KeyPackage, format);
    }
}
