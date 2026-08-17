using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Settings;

/// <summary>
/// Typed accessor over the Setting key/value table, with an in-memory cache and transparent
/// encryption of the secret value (the SendGrid API key) via <see cref="ISecretProtector"/>.
/// </summary>
public sealed class SettingsService(
    IDbContextFactory<AppDbContext> factory,
    ISecretProtector protector,
    IOptions<EngineOptions> engineOptions)
    : ISettingsService
{
    private const string ApiKeyKey = "Email:ApiKey";
    private const string FromEmailKey = "Email:FromEmail";
    private const string FromNameKey = "Email:FromName";
    private const string ToEmailKey = "Email:ToEmail";
    private const string RawRetentionDaysKey = "Retention:RawDays";

    private readonly EngineOptions _engineOptions = engineOptions.Value;
    private volatile EmailSettings? _cachedEmail;
    private volatile RetentionSettings? _cachedRetention;

    public async Task<EmailSettings> GetEmailAsync(CancellationToken ct = default)
    {
        if (_cachedEmail is not null) return _cachedEmail;

        await using var db = await factory.CreateDbContextAsync(ct);
        string[] keys = [ApiKeyKey, FromEmailKey, FromNameKey, ToEmailKey];
        var rows = await db.Settings.Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var email = new EmailSettings
        {
            ApiKey = Reveal(rows.GetValueOrDefault(ApiKeyKey)),
            FromEmail = rows.GetValueOrDefault(FromEmailKey),
            FromName = rows.GetValueOrDefault(FromNameKey),
            ToEmail = rows.GetValueOrDefault(ToEmailKey),
        };
        _cachedEmail = email;
        return email;
    }

    public async Task SaveEmailAsync(EmailSettings settings, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Only replace the API key when a new one is supplied (blank = keep existing).
        if (!string.IsNullOrEmpty(settings.ApiKey))
            await UpsertAsync(db, ApiKeyKey, protector.Protect(settings.ApiKey), secret: true, ct);

        await UpsertAsync(db, FromEmailKey, settings.FromEmail, secret: false, ct);
        await UpsertAsync(db, FromNameKey, settings.FromName, secret: false, ct);
        await UpsertAsync(db, ToEmailKey, settings.ToEmail, secret: false, ct);

        await db.SaveChangesAsync(ct);
        _cachedEmail = null; // invalidate cache
    }

    public async Task<RetentionSettings> GetRetentionAsync(CancellationToken ct = default)
    {
        if (_cachedRetention is not null) return _cachedRetention;

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == RawRetentionDaysKey, ct);
        var rawDays = int.TryParse(row?.Value, out var d) && d > 0 ? d : _engineOptions.RawRetentionDays;

        return _cachedRetention = new RetentionSettings { RawDays = rawDays };
    }

    public async Task SaveRetentionAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rawDays = Math.Max(1, settings.RawDays);
        await UpsertAsync(db, RawRetentionDaysKey, rawDays.ToString(), secret: false, ct);
        await db.SaveChangesAsync(ct);
        _cachedRetention = null; // invalidate cache
    }

    private string? Reveal(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext;
        try { return protector.Unprotect(ciphertext); }
        catch { return null; } // key lost/rotated — treat as unset rather than crash
    }

    private static async Task UpsertAsync(AppDbContext db, string key, string? value, bool secret, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = key, Value = value, IsSecret = secret });
        else
        {
            row.Value = value;
            row.IsSecret = secret;
        }
    }
}
