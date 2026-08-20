using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Domain;

namespace MT.Uptime.Core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Monitor> Monitors => Set<Monitor>();
    public DbSet<Heartbeat> Heartbeats => Set<Heartbeat>();
    public DbSet<MonitorEvent> MonitorEvents => Set<MonitorEvent>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentUpdate> IncidentUpdates => Set<IncidentUpdate>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<MonitorNotification> MonitorNotifications => Set<MonitorNotification>();
    public DbSet<StatusPage> StatusPages => Set<StatusPage>();
    public DbSet<StatusPageMonitor> StatusPageMonitors => Set<StatusPageMonitor>();
    public DbSet<StatRollup> StatRollups => Set<StatRollup>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<MonitorTag> MonitorTags => Set<MonitorTag>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<MaintenanceWindowMonitor> MaintenanceWindowMonitors => Set<MaintenanceWindowMonitor>();
    public DbSet<MaintenanceWindowTag> MaintenanceWindowTags => Set<MaintenanceWindowTag>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.Username).HasMaxLength(256);
            // NOCASE, for the same reason as Tag.Name below and with more at stake: sign-in compares this
            // column, so under the default binary collation an account created as "Matt" rejects "matt"
            // — and the login page reports that as "Invalid username or password", which sends the
            // operator hunting for a password problem that does not exist.
            //
            // It also makes the unique index case-insensitive, so "Matt" and "matt" can no longer be two
            // accounts. That is the intended behaviour: two accounts distinguishable only by case is a
            // phishing surface, not a feature. SQLite's NOCASE is ASCII-only, the same trade the rest of
            // the schema makes.
            e.Property(u => u.Username).UseCollation("NOCASE");
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(256);
            e.Property(u => u.Email).HasMaxLength(320);              // max length of an email address per RFC 5321
            // NOCASE and unique, for the same reasons as Username above. Email is what
            // BeginPasswordResetAsync resolves an account by, so both properties are load-bearing:
            //
            //   Uniqueness — two accounts sharing an address makes "reset the password for this email"
            //   ambiguous, and the resolution was an unordered FirstOrDefaultAsync. Nothing escalated,
            //   because the administrator is row 1 and SQLite's AUTOINCREMENT never reuses a rowid, so
            //   the first match was always the same row. But that is an accident of insertion order
            //   holding up an authentication boundary, not a rule, and it is one index change away from
            //   mattering. The statement is now ordered as well — see BeginPasswordResetAsync — so the
            //   invariant does not rest on the index alone either.
            //
            //   NOCASE — an operator who registers "Matt@example.com" and later types
            //   "matt@example.com" would otherwise get the deliberately vague "if that address exists,
            //   we sent a link" and never receive one. That is the same silent failure the username
            //   collation fixed, on the one path that exists to recover an account nobody can sign in to.
            //
            // NULL is exempt: SQLite treats NULLs as distinct in a unique index, so any number of
            // accounts may have no address at all. That is deliberate — an email is optional here.
            e.Property(u => u.Email).UseCollation("NOCASE");
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.PasswordResetTokenHash).HasMaxLength(64); // hex SHA-256
        });

        b.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
        });

        b.Entity<Monitor>(e =>
        {
            e.Property(m => m.Name).HasMaxLength(256);
            e.HasIndex(m => m.Enabled);
        });

        b.Entity<Heartbeat>(e =>
        {
            e.HasIndex(h => new { h.MonitorId, h.Timestamp });
            e.HasOne(h => h.Monitor)
                .WithMany(m => m.Heartbeats)
                .HasForeignKey(h => h.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MonitorEvent>(e =>
        {
            e.HasIndex(ev => new { ev.MonitorId, ev.StartedAt });
            e.HasOne(ev => ev.Monitor)
                .WithMany(m => m.Events)
                .HasForeignKey(ev => ev.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
            // SetNull, not Cascade: the event is the durable per-monitor record and the incident is only
            // a grouping over it, so discarding a grouping must never delete the history it grouped.
            e.HasOne(ev => ev.Incident)
                .WithMany(i => i.Events)
                .HasForeignKey(ev => ev.IncidentId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(ev => ev.IncidentId);
        });

        b.Entity<Incident>(e =>
        {
            e.Ignore(i => i.IsOpen);
            e.Property(i => i.Title).HasMaxLength(256);
            e.Property(i => i.CorrelationKey).HasMaxLength(256);
            // "the open incident on this key" is the correlation lookup, run once per outage transition.
            e.HasIndex(i => new { i.CorrelationKey, i.ResolvedAt });
            e.HasIndex(i => i.StartedAt);
            // Deleting the account that acknowledged an incident must not delete the incident, so the
            // acknowledgement simply loses its attribution.
            e.HasOne(i => i.AcknowledgedBy)
                .WithMany()
                .HasForeignKey(i => i.AcknowledgedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<NotificationChannel>(e =>
        {
            e.Property(n => n.Name).HasMaxLength(256);
        });

        b.Entity<MonitorNotification>(e =>
        {
            e.HasKey(mn => new { mn.MonitorId, mn.NotificationChannelId });
            e.HasOne(mn => mn.Monitor)
                .WithMany(m => m.Notifications)
                .HasForeignKey(mn => mn.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(mn => mn.NotificationChannel)
                .WithMany(n => n.Monitors)
                .HasForeignKey(mn => mn.NotificationChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StatusPage>(e =>
        {
            e.Property(sp => sp.Slug).HasMaxLength(128);
            e.Property(sp => sp.Title).HasMaxLength(256);
            e.HasIndex(sp => sp.Slug).IsUnique();
        });

        b.Entity<StatusPageMonitor>(e =>
        {
            e.HasKey(spm => new { spm.StatusPageId, spm.MonitorId });
            e.HasOne(spm => spm.StatusPage)
                .WithMany(sp => sp.Monitors)
                .HasForeignKey(spm => spm.StatusPageId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(spm => spm.Monitor)
                .WithMany(m => m.StatusPageLinks)
                .HasForeignKey(spm => spm.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(64);
            e.Property(t => t.Colour).HasMaxLength(7);   // "#RRGGBB"
            // NOCASE, so "Prod" and "prod" collide instead of becoming two tags that each match half
            // the monitors. SQLite's NOCASE is ASCII-only, which is the same trade the rest of the
            // schema makes and is fine for what a tag name is.
            e.Property(t => t.Name).UseCollation("NOCASE");
            e.HasIndex(t => t.Name).IsUnique();
        });

        b.Entity<MonitorTag>(e =>
        {
            e.HasKey(mt => new { mt.MonitorId, mt.TagId });
            e.HasOne(mt => mt.Monitor)
                .WithMany(m => m.Tags)
                .HasForeignKey(mt => mt.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a tag unassigns it everywhere rather than being blocked by its assignments —
            // a tag is a label, so removing it should never require visiting every monitor first.
            e.HasOne(mt => mt.Tag)
                .WithMany(t => t.Monitors)
                .HasForeignKey(mt => mt.TagId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(mt => mt.TagId);   // "every monitor carrying this tag" is the filter query
        });

        b.Entity<IncidentUpdate>(e =>
        {
            e.HasIndex(u => new { u.IncidentId, u.PostedAt });
            e.HasOne(u => u.Incident)
                .WithMany(i => i.Updates)
                .HasForeignKey(u => u.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting the author leaves the note in place — it is published history by then.
            e.HasOne(u => u.PostedBy)
                .WithMany()
                .HasForeignKey(u => u.PostedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MaintenanceWindow>(e =>
        {
            e.Property(w => w.Name).HasMaxLength(256);
            e.Property(w => w.TimeZoneId).HasMaxLength(128);
            e.HasIndex(w => w.Enabled);
        });

        b.Entity<MaintenanceWindowMonitor>(e =>
        {
            e.HasKey(x => new { x.MaintenanceWindowId, x.MonitorId });
            e.HasOne(x => x.MaintenanceWindow)
                .WithMany(w => w.Monitors)
                .HasForeignKey(x => x.MaintenanceWindowId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting a monitor simply narrows the window's scope rather than being blocked by it.
            e.HasOne(x => x.Monitor)
                .WithMany()
                .HasForeignKey(x => x.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.MonitorId);
        });

        b.Entity<MaintenanceWindowTag>(e =>
        {
            e.HasKey(x => new { x.MaintenanceWindowId, x.TagId });
            e.HasOne(x => x.MaintenanceWindow)
                .WithMany(w => w.Tags)
                .HasForeignKey(x => x.MaintenanceWindowId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag)
                .WithMany()
                .HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TagId);
        });

        b.Entity<StatRollup>(e =>
        {
            e.Ignore(r => r.Total);
            e.Ignore(r => r.AvailableCount);
            e.HasIndex(r => new { r.MonitorId, r.Period, r.BucketStart }).IsUnique();
            e.HasOne(r => r.Monitor)
                .WithMany()
                .HasForeignKey(r => r.MonitorId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
