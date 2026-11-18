using COMS_MVC.Data;
using COMS_MVC.Hubs;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace COMS.Tests;

/// <summary>
/// Pagination, bulk read, and bulk delete must be server-side and scoped to
/// the authenticated user. Other users' notifications are never affected.
/// </summary>
public sealed class NotificationBulkTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly ApplicationDbContext _db;

    public NotificationBulkTests()
    {
        _conn.Open();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private sealed class NullProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NullProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excluded) => Proxy;
        public IClientProxy Client(string id) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> ids) => Proxy;
        public IClientProxy Group(string name) => Proxy;
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> excluded) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> names) => Proxy;
        public IClientProxy User(string id) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> ids) => Proxy;
    }

    private sealed class NullHubContext : IHubContext<MonitoringHub>
    {
        public IHubClients Clients { get; } = new NullHubClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private NotificationService Service() =>
        new(_db, NullLogger<NotificationService>.Instance,
            new NullHubContext(), TestUserManagers.CreateUserManager(_db));

    private async Task<int> CreateUserIdAsync(string userName)
    {
        var mgr = TestUserManagers.CreateUserManager(_db);
        var user = new ApplicationUser
        {
            UserName = userName,
            Email = $"{userName}@example.com",
            FullName = userName,
            EmailConfirmed = true
        };
        var result = await mgr.CreateAsync(user, "Secret@123!");
        Assert.True(result.Succeeded);
        return user.Id;
    }

    private async Task SeedAsync(int userId, int count, bool read = false)
    {
        var start = DateTime.UtcNow.AddHours(-count);
        for (var i = 0; i < count; i++)
        {
            _db.Notifications.Add(new Notification
            {
                UserId = userId,
                Title = $"Notification {i}",
                Message = "Message",
                Type = "Report",
                IsRead = read,
                CreatedAt = start.AddMinutes(i)
            });
        }
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPage_ReturnsRequestedSlice_AndCounts()
    {
        var userId = await CreateUserIdAsync("notif1");
        await SeedAsync(userId, 25);
        var service = Service();

        var first = await service.GetPageAsync(userId, 1, 10);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(25, first.TotalCount);
        Assert.Equal(25, first.UnreadCount);
        Assert.Equal(3, first.TotalPages);

        var last = await service.GetPageAsync(userId, 3, 10);
        Assert.Equal(5, last.Items.Count);
    }

    [Fact]
    public async Task MarkAllAsRead_OnlyAffectsCurrentUser()
    {
        var mine = await CreateUserIdAsync("notif2");
        var other = await CreateUserIdAsync("notif3");
        await SeedAsync(mine, 4);
        await SeedAsync(other, 3);
        var service = Service();

        var updated = await service.MarkAllAsReadAsync(mine);

        Assert.Equal(4, updated);
        Assert.Equal(0, await _db.Notifications.CountAsync(n => n.UserId == mine && !n.IsRead));
        Assert.Equal(3, await _db.Notifications.CountAsync(n => n.UserId == other && !n.IsRead));
    }

    [Fact]
    public async Task DeleteAll_OnlyDeletesCurrentUserRows()
    {
        var mine = await CreateUserIdAsync("notif4");
        var other = await CreateUserIdAsync("notif5");
        await SeedAsync(mine, 6);
        await SeedAsync(other, 2);
        var service = Service();

        var deleted = await service.DeleteAllAsync(mine);

        Assert.Equal(6, deleted);
        Assert.Equal(0, await _db.Notifications.CountAsync(n => n.UserId == mine));
        Assert.Equal(2, await _db.Notifications.CountAsync(n => n.UserId == other));
    }
}
