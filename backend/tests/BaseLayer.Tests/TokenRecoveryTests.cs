using System.Net;
using BaseLayer.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseLayer.Tests;

public sealed partial class PlatformTests
{
    [Fact]
    public async Task UnauthorizedReadRefreshesPersistsAndRecoversUsage()
    {
        var home = await Connect();
        await Allow(home);
        var reads = provider.Reads;
        provider.UnauthorizedReads = 1;
        clock.Advance(30);

        await service.PollAsync(home.Id);

        Assert.Equal(2, provider.Reads - reads);
        Assert.Equal(1, provider.Refreshes);
        Assert.Equal("renewed", provider.LastReadTokens!.AccessToken);
        db.ChangeTracker.Clear();
        var saved = await db.Homes.SingleAsync();
        var tokens = new PlainProtector().Unprotect(saved.ProtectedTokens!);
        Assert.Equal("renewed", tokens.AccessToken);
        Assert.Equal("refresh", tokens.RefreshToken);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, saved.LastSeenUtc);
        Assert.Equal(13000d, saved.HouseholdWatts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedRecoveryDoesNotLoopOrMarkReadingsFresh(bool refreshRejected)
    {
        var home = await Connect();
        var lastSeen = (await db.Homes.SingleAsync()).LastSeenUtc;
        var reads = provider.Reads;
        provider.UnauthorizedReads = 2;
        provider.FailRefresh = refreshRejected;
        clock.Advance(30);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.PollAsync(home.Id));

        Assert.Equal(refreshRejected ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(1, provider.Refreshes);
        Assert.Equal(refreshRejected ? 1 : 2, provider.Reads - reads);
        Assert.Equal(lastSeen, (await db.Homes.SingleAsync()).LastSeenUtc);
    }

    [Fact]
    public async Task OtherReadFailuresDoNotRefreshTokens()
    {
        var home = await Connect();
        var reads = provider.Reads;
        provider.FailRead = true;

        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollAsync(home.Id));

        Assert.Equal(0, provider.Refreshes);
        Assert.Equal(1, provider.Reads - reads);
    }

    [Fact]
    public async Task ExpiringTokensStillRefreshBeforeReading()
    {
        var home = await Connect();
        var saved = await db.Homes.SingleAsync();
        saved.ProtectedTokens = new PlainProtector().Protect(new ProviderTokens("old", "refresh", clock.GetUtcNow().UtcDateTime.AddSeconds(30)));
        await db.SaveChangesAsync();
        var reads = provider.Reads;

        await service.PollAsync(home.Id);

        Assert.Equal(1, provider.Refreshes);
        Assert.Equal(1, provider.Reads - reads);
        Assert.Equal("renewed", provider.LastReadTokens!.AccessToken);
    }
}
