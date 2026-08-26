using InvitesBlog.Domain.Enums;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Contacts;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The linking rules that keep an uploaded guest row from becoming an identity claim. Guest lists
/// are uploaded by anyone and verified by nobody, so a pairing found there may only decide what we
/// OFFER — access has to come from a code the person actually received.
/// </summary>
public class ContactLinkServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly IRepository<Invite> _invites = Substitute.For<IRepository<Invite>>();
    private readonly IRepository<VerifiedContactLink> _links = Substitute.For<IRepository<VerifiedContactLink>>();
    private readonly IOtpService _otp = Substitute.For<IOtpService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private const string Phone = "+9607819157";
    private const string Email = "ahmed@example.com";
    private const string MaskedEmail = "a•••d@example.com";

    private ContactLinkService Sut() => new(_currentUser, _guests, _invites, _links, _otp, _uow);

    private void SignedInByPhone(string phone = Phone)
    {
        _currentUser.ContactType.Returns("phone");
        _currentUser.Contact.Returns(phone);
    }

    private static Guest GuestRow(string? email, string? phone) => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = Guid.NewGuid(),
        Email = email,
        PhoneE164 = phone,
        Name = "Ahmed"
    };

    private void NoExistingLinks() =>
        _links.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<VerifiedContactLink, bool>>>(),
            Arg.Any<CancellationToken>()).Returns((IReadOnlyList<VerifiedContactLink>)new List<VerifiedContactLink>());

    // ----- discovery -----

    [Fact]
    public async Task Offers_the_email_a_host_put_beside_the_signed_in_number()
    {
        SignedInByPhone();
        NoExistingLinks();
        var guest = GuestRow(Email, Phone);
        _guests.Query().Returns(new[] { guest }.AsAsyncQueryable());
        _invites.Query().Returns(new[] { new Invite { Id = Guid.NewGuid(), GuestId = guest.Id } }.AsAsyncQueryable());

        var offers = await Sut().GetLinkableAsync();

        var offer = Assert.Single(offers);
        Assert.Equal("email", offer.ContactType);
        Assert.Equal(MaskedEmail, offer.Masked);
        Assert.Equal(1, offer.InviteCount);
    }

    [Fact]
    public async Task Offer_is_masked_so_the_list_never_discloses_a_full_address()
    {
        SignedInByPhone();
        NoExistingLinks();
        var guest = GuestRow(Email, Phone);
        _guests.Query().Returns(new[] { guest }.AsAsyncQueryable());
        _invites.Query().Returns(new[] { new Invite { Id = Guid.NewGuid(), GuestId = guest.Id } }.AsAsyncQueryable());

        var offers = await Sut().GetLinkableAsync();

        Assert.DoesNotContain(Email, Assert.Single(offers).Masked);
    }

    [Fact]
    public async Task Offers_nothing_when_no_guest_row_pairs_the_number_with_an_email()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(new[] { GuestRow(null, Phone) }.AsAsyncQueryable());

        Assert.Empty(await Sut().GetLinkableAsync());
    }

    [Fact]
    public async Task Offers_nothing_when_the_paired_email_has_no_invitations_to_add()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(new[] { GuestRow(Email, Phone) }.AsAsyncQueryable());
        _invites.Query().Returns(Array.Empty<Invite>().AsAsyncQueryable());

        Assert.Empty(await Sut().GetLinkableAsync());
    }

    [Fact]
    public async Task Already_linked_contact_is_not_offered_again()
    {
        SignedInByPhone();
        var guest = GuestRow(Email, Phone);
        _guests.Query().Returns(new[] { guest }.AsAsyncQueryable());
        _invites.Query().Returns(new[] { new Invite { Id = Guid.NewGuid(), GuestId = guest.Id } }.AsAsyncQueryable());
        _links.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<VerifiedContactLink, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<VerifiedContactLink>)new List<VerifiedContactLink>
            {
                new() { Id = Guid.NewGuid(), Email = Email, PhoneE164 = Phone, VerifiedFrom = "phone" }
            });

        Assert.Empty(await Sut().GetLinkableAsync());
    }

    // ----- the security boundary -----

    [Fact]
    public async Task Verifying_a_contact_that_was_never_offered_is_refused_and_stores_nothing()
    {
        // The attacker holds a real code for an address they control, but that address is not paired
        // with their number on any guest row — proving a contact must not by itself widen an inbox.
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(Array.Empty<Guest>().AsAsyncQueryable());
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("email", "victim@example.com"));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().VerifyLinkAsync(Guid.NewGuid(), "123456"));

        Assert.Equal("contact_not_linkable", ex.ErrorCode);
        await _links.DidNotReceive().AddAsync(Arg.Any<VerifiedContactLink>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requesting_a_code_for_a_contact_that_was_never_offered_sends_nothing()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(Array.Empty<Guest>().AsAsyncQueryable());

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().RequestLinkCodeAsync("s•••r@example.com"));

        await _otp.DidNotReceive().RequestAsync(Arg.Any<SendOtpRequest>(), Arg.Any<OtpPurpose>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_verifying_the_contact_already_signed_in_with_is_refused()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(Array.Empty<Guest>().AsAsyncQueryable());
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("phone", Phone));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().VerifyLinkAsync(Guid.NewGuid(), "123456"));

        Assert.Equal("contact_already_verified", ex.ErrorCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_gets_nothing()
    {
        _currentUser.ContactType.Returns((string?)null);
        _currentUser.Contact.Returns((string?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().GetLinkableAsync());
    }

    // ----- the happy path -----

    [Fact]
    public async Task Proving_an_offered_contact_records_the_pairing()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(new[] { GuestRow(Email, Phone) }.AsAsyncQueryable());
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("email", Email));

        VerifiedContactLink? saved = null;
        await _links.AddAsync(Arg.Do<VerifiedContactLink>(l => saved = l), Arg.Any<CancellationToken>());

        var result = await Sut().VerifyLinkAsync(Guid.NewGuid(), "123456");

        Assert.True(result.Linked);
        Assert.NotNull(saved);
        Assert.Equal(Email, saved!.Email);
        Assert.Equal(Phone, saved.PhoneE164);
        Assert.Equal("phone", saved.VerifiedFrom);
        await _uow.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Proving_a_pairing_that_is_already_on_file_is_a_no_op()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(new[] { GuestRow(Email, Phone) }.AsAsyncQueryable());
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("email", Email));
        _links.FirstOrDefaultAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<VerifiedContactLink, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new VerifiedContactLink { Id = Guid.NewGuid(), Email = Email, PhoneE164 = Phone, VerifiedFrom = "phone" });

        var result = await Sut().VerifyLinkAsync(Guid.NewGuid(), "123456");

        Assert.False(result.Linked);
        await _links.DidNotReceive().AddAsync(Arg.Any<VerifiedContactLink>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requesting_a_code_for_an_offered_contact_sends_to_that_contact()
    {
        SignedInByPhone();
        NoExistingLinks();
        _guests.Query().Returns(new[] { GuestRow(Email, Phone) }.AsAsyncQueryable());
        var challengeId = Guid.NewGuid();
        _otp.RequestAsync(Arg.Any<SendOtpRequest>(), Arg.Any<OtpPurpose>(), Arg.Any<CancellationToken>())
            .Returns(new OtpChallengeResponse(challengeId, 300));

        var id = await Sut().RequestLinkCodeAsync(MaskedEmail);

        Assert.Equal(challengeId, id);
        await _otp.Received().RequestAsync(
            Arg.Is<SendOtpRequest>(r => r.Channel == "email" && r.Email == Email),
            OtpPurpose.SignIn, Arg.Any<CancellationToken>());
    }
}
