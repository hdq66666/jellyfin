using System;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class MediaStreamRepositoryColorRangeTests : SqliteDbTestFixture
{
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly MediaStreamRepository _repository;

    public MediaStreamRepositoryColorRangeTests()
    {
        using var context = CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = _itemId,
            Type = "MediaBrowser.Controller.Entities.Movies.Movie",
            Name = "HDR sample"
        });
        context.SaveChanges();
        _repository = new MediaStreamRepository(
            CreateDbContextFactory(),
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<ILocalizationManager>());
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("pc")]
    [InlineData(null)]
    public void SaveAndRead_RoundTripsColorRange(string? range)
    {
        var stream = new MediaStream
        {
            Index = 0,
            Type = MediaStreamType.Video,
            Codec = "av1",
            ColorPrimaries = "bt2020",
            ColorSpace = "bt2020nc",
            ColorTransfer = "smpte2084",
            ColorRange = range
        };

        _repository.SaveMediaStreams(_itemId, [stream], CancellationToken.None);

        using (var context = CreateDbContext())
        {
            Assert.Equal(range, Assert.Single(context.MediaStreamInfos.Where(s => s.ItemId.Equals(_itemId))).ColorRange);
        }

        var restored = Assert.Single(_repository.GetMediaStreams(new MediaStreamQuery { ItemId = _itemId }));
        Assert.Equal(range, restored.ColorRange);
        Assert.Equal("bt2020", restored.ColorPrimaries);
        Assert.Equal("bt2020nc", restored.ColorSpace);
        Assert.Equal("smpte2084", restored.ColorTransfer);
    }

    [Theory]
    [InlineData("pc")]
    [InlineData(null)]
    public void SaveMediaStreams_ReplacesExistingColorRange(string? replacement)
    {
        var stream = new MediaStream { Index = 0, Type = MediaStreamType.Video, ColorRange = "tv" };
        _repository.SaveMediaStreams(_itemId, [stream], CancellationToken.None);
        stream.ColorRange = replacement;
        _repository.SaveMediaStreams(_itemId, [stream], CancellationToken.None);

        Assert.Equal(replacement, Assert.Single(_repository.GetMediaStreams(new MediaStreamQuery { ItemId = _itemId })).ColorRange);
    }

    [Fact]
    public void Upgrade_LeavesExistingColorRangeUnknownAndPreservesMetadata()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;
        using var context = new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(Mock.Of<IApplicationPaths>(), NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        var migrator = context.GetService<IMigrator>();
        migrator.Migrate("20261002210711_MakeOwnerIdIndexesPartial");

        var itemId = Guid.NewGuid();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = itemId,
            Type = "MediaBrowser.Controller.Entities.Movies.Movie",
            Name = "Existing HDR sample"
        });
        context.SaveChanges();
        context.Database.ExecuteSqlInterpolated($"INSERT INTO MediaStreamInfos (ItemId, StreamIndex, StreamType, IsDefault, IsForced, IsExternal, IsOriginal, ColorPrimaries, ColorSpace, ColorTransfer) VALUES ({itemId}, 0, 1, 0, 0, 0, 0, 'bt2020', 'bt2020nc', 'smpte2084')");

        migrator.Migrate();
        context.ChangeTracker.Clear();
        var stream = Assert.Single(context.MediaStreamInfos.Where(s => s.ItemId.Equals(itemId)));
        Assert.Null(stream.ColorRange);
        Assert.Equal("bt2020", stream.ColorPrimaries);
        Assert.Equal("bt2020nc", stream.ColorSpace);
        Assert.Equal("smpte2084", stream.ColorTransfer);
        Assert.Contains("20261105134119_AddMediaStreamColorRange", context.Database.GetAppliedMigrations());
    }
}
