using HrDashboard.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HrDashboard.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Conversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.UserId).HasMaxLength(450).IsRequired();
            e.Property(c => c.Title).HasMaxLength(512);
            e.HasMany(c => c.Messages)
             .WithOne(m => m.Conversation)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.UserId);
        });

        builder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Content).IsRequired();
            e.Property(m => m.Role).HasConversion<string>();
        });

        builder.Entity<UsageRecord>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.UserId).HasMaxLength(450).IsRequired();
            e.Property(u => u.Provider).HasMaxLength(100).IsRequired();
            e.Property(u => u.Model).HasMaxLength(200).IsRequired();
            e.HasOne(u => u.Message)
             .WithMany()
             .HasForeignKey(u => u.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.UserId);
            e.HasIndex(u => u.CreatedAt);
        });
    }
}
