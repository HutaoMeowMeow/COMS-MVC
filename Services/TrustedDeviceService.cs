using Microsoft.Extensions.Options;

namespace COMS_MVC.Services
{
    public class TwoFactorOptions
    {
        public const string SectionName = "TwoFactor";
        public const int MaxTrustedDeviceDays = 30;

        private int _trustedDeviceDays = MaxTrustedDeviceDays;
        private int _cleanupIntervalMinutes = 60;

        public int TrustedDeviceDays
        {
            get => _trustedDeviceDays;
            set => _trustedDeviceDays = Math.Clamp(value, 1, MaxTrustedDeviceDays);
        }

        public int CleanupIntervalMinutes
        {
            get => _cleanupIntervalMinutes;
            set => _cleanupIntervalMinutes = Math.Clamp(value, 1, 1440);
        }

        public TimeSpan TrustedDeviceLifetime => TimeSpan.FromDays(TrustedDeviceDays);
    }

    public interface ITrustedDeviceService
    {
        Task<TrustedDevice> CreateAsync(int userId, string deviceName, string? userAgent, string token);
        Task<bool> HasValidTrustedDeviceAsync(int userId, string? token);
        Task TouchAsync(int userId, string token);
        Task<List<TrustedDevice>> GetDevicesAsync(int userId);
        Task<List<TrustedDevice>> GetActiveDevicesAsync(int userId);
        Task<bool> RevokeAsync(int userId, int deviceId);
        Task<bool> RevokeAllAsync(int userId);
        Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
    }

    public class TrustedDeviceService : ITrustedDeviceService
    {
        private const int MaxDeviceNameLength = 200;
        private const int MaxUserAgentLength = 500;

        private readonly ApplicationDbContext _db;
        private readonly ILogger<TrustedDeviceService> _logger;
        private readonly TwoFactorOptions _options;
        private readonly TimeProvider _timeProvider;

        public TrustedDeviceService(
            ApplicationDbContext db,
            ILogger<TrustedDeviceService> logger,
            IOptions<TwoFactorOptions> options,
            TimeProvider timeProvider)
        {
            _db = db;
            _logger = logger;
            _options = options.Value;
            _timeProvider = timeProvider;
        }

        public async Task<TrustedDevice> CreateAsync(int userId, string deviceName, string? userAgent, string token)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var device = new TrustedDevice
            {
                UserId = userId,
                DeviceName = Truncate(
                    string.IsNullOrWhiteSpace(deviceName) ? "Unknown device" : deviceName.Trim(),
                    MaxDeviceNameLength),
                TokenHash = HashToken(token),
                UserAgent = TruncateOptional(userAgent, MaxUserAgentLength),
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
                ExpiresAtUtc = now.Add(_options.TrustedDeviceLifetime)
            };

            _db.TrustedDevices.Add(device);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Trusted device created for user {UserId}.", userId);
            return device;
        }

        public async Task<bool> HasValidTrustedDeviceAsync(int userId, string? token)
        {
            if (string.IsNullOrEmpty(token))
                return false;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var hash = HashToken(token);
            var device = await _db.TrustedDevices
                .Where(d => d.UserId == userId
                    && d.TokenHash == hash
                    && d.ExpiresAtUtc > now)
                .OrderByDescending(d => d.LastSeenAtUtc)
                .FirstOrDefaultAsync();

            return device != null;
        }

        public async Task TouchAsync(int userId, string token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var hash = HashToken(token);
            var device = await _db.TrustedDevices
                .Where(d => d.UserId == userId
                    && d.TokenHash == hash
                    && d.ExpiresAtUtc > now)
                .OrderByDescending(d => d.LastSeenAtUtc)
                .FirstOrDefaultAsync();

            if (device != null)
            {
                device.LastSeenAtUtc = now;
                await _db.SaveChangesAsync();
            }
        }

        public async Task<List<TrustedDevice>> GetDevicesAsync(int userId)
        {
            return await _db.TrustedDevices
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.LastSeenAtUtc)
                .ToListAsync();
        }

        public async Task<List<TrustedDevice>> GetActiveDevicesAsync(int userId)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            return await _db.TrustedDevices
                .Where(d => d.UserId == userId && d.ExpiresAtUtc > now)
                .OrderByDescending(d => d.LastSeenAtUtc)
                .ToListAsync();
        }

        public async Task<bool> RevokeAsync(int userId, int deviceId)
        {
            var device = await _db.TrustedDevices
                .Where(d => d.UserId == userId && d.TrustedDeviceId == deviceId)
                .FirstOrDefaultAsync();

            if (device is null)
                return false;

            _db.TrustedDevices.Remove(device);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> RevokeAllAsync(int userId)
        {
            var count = await _db.TrustedDevices
                .Where(d => d.UserId == userId)
                .ExecuteDeleteAsync();

            if (count > 0)
                _logger.LogInformation("Revoked {Count} trusted devices for user {UserId}.", count, userId);

            return count > 0;
        }

        public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var count = await _db.TrustedDevices
                .Where(d => d.ExpiresAtUtc <= now)
                .ExecuteDeleteAsync(cancellationToken);

            if (count > 0)
                _logger.LogInformation("Purged {Count} expired trusted devices.", count);

            return count;
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        private static string? TruncateOptional(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private static string HashToken(string token) =>
            Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(token)));
    }
}

