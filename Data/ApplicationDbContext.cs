using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace COMS_MVC.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser, IdentityRole<int>, int>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Canal> Canals { get; set; }

        public DbSet<Sensor> Sensors { get; set; }

        public DbSet<SensorReading> SensorReadings { get; set; }

        public DbSet<ObstructionAlert> ObstructionAlerts { get; set; }

        public DbSet<CommunityReport> CommunityReports { get; set; }

        public DbSet<FloodRiskAssessment> FloodRiskAssessments { get; set; }

        public DbSet<Notification> Notifications { get; set; }

        public DbSet<Announcement> Announcements { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Canal>(entity =>
            {
                entity.HasKey(e => e.CanalId);
                entity.Property(e => e.CanalName).IsRequired().HasMaxLength(150);
                entity.Property(e => e.Location).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Barangay).IsRequired().HasMaxLength(100);
                entity.Property(e => e.City).IsRequired().HasMaxLength(100);
                entity.HasIndex(e => e.CanalName);
            });

            builder.Entity<Sensor>(entity =>
            {
                entity.HasKey(e => e.SensorId);
                entity.Property(e => e.SensorCode).IsRequired().HasMaxLength(50);
                entity.Property(e => e.SensorType).IsRequired().HasMaxLength(50);
                entity.HasIndex(e => e.SensorCode).IsUnique();
                entity.HasOne(e => e.Canal)
                      .WithMany(c => c.Sensors)
                      .HasForeignKey(e => e.CanalId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<SensorReading>(entity =>
            {
                entity.HasKey(e => e.SensorReadingId);
                entity.HasIndex(e => e.RecordedAt);
                entity.HasOne(e => e.Sensor)
                      .WithMany(s => s.SensorReadings)
                      .HasForeignKey(e => e.SensorId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.Canal)
                      .WithMany(c => c.SensorReadings)
                      .HasForeignKey(e => e.CanalId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<ObstructionAlert>(entity =>
            {
                entity.HasKey(e => e.ObstructionAlertId);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(150);
                entity.HasIndex(e => e.DetectedAt);
                entity.HasIndex(e => new { e.CanalId, e.Status });
                entity.HasOne(e => e.Canal)
                      .WithMany(c => c.Alerts)
                      .HasForeignKey(e => e.CanalId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.SensorReading)
                      .WithMany(sr => sr.Alerts)
                      .HasForeignKey(e => e.SensorReadingId)
                      .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(e => e.AssignedToUser)
                      .WithMany()
                      .HasForeignKey(e => e.AssignedToUserId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<CommunityReport>(entity =>
            {
                entity.HasKey(e => e.CommunityReportId);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(150);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => new { e.UserId, e.Status });
                entity.HasOne(e => e.User)
                      .WithMany(u => u.CommunityReports)
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.Canal)
                      .WithMany(c => c.CommunityReports)
                      .HasForeignKey(e => e.CanalId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<FloodRiskAssessment>(entity =>
            {
                entity.HasKey(e => e.FloodRiskAssessmentId);
                entity.HasIndex(e => e.AssessmentDate);
                entity.HasOne(e => e.Canal)
                      .WithMany(c => c.FloodRiskAssessments)
                      .HasForeignKey(e => e.CanalId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<Notification>(entity =>
            {
                entity.HasKey(e => e.NotificationId);
                entity.HasIndex(e => new { e.UserId, e.IsRead });
                entity.HasOne(e => e.User)
                      .WithMany(u => u.Notifications)
                      .HasForeignKey(e => e.UserId)
                      .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(e => e.RelatedAlert)
                      .WithMany()
                      .HasForeignKey(e => e.RelatedAlertId)
                      .OnDelete(DeleteBehavior.SetNull);
                entity.HasOne(e => e.RelatedReport)
                      .WithMany()
                      .HasForeignKey(e => e.RelatedReportId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<Announcement>(entity =>
            {
                entity.HasKey(e => e.AnnouncementId);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(150);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasOne(e => e.PostedBy)
                      .WithMany(u => u.PostedAnnouncements)
                      .HasForeignKey(e => e.PostedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(u => u.FullName).IsRequired().HasMaxLength(100);
            });
        }
    }
}
