using Microsoft.EntityFrameworkCore;
using SafetyVision.Data.Entities;

namespace SafetyVision.Data;

public sealed class SafetyVisionDbContext(DbContextOptions<SafetyVisionDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<InspectionItem> InspectionItems => Set<InspectionItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(u => u.Id);
            e.Property(u => u.LoginId).HasMaxLength(64).IsRequired();
            e.HasIndex(u => u.LoginId).IsUnique();
            e.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
            e.Property(u => u.Name).HasMaxLength(100).IsRequired();
            e.Property(u => u.CreatedAt).HasColumnType("datetime(6)");
        });

        modelBuilder.Entity<Inspection>(e =>
        {
            e.ToTable("Inspections");
            e.HasKey(i => i.Id);
            e.Property(i => i.InspectionKey).HasColumnType("char(36)").IsRequired();
            e.HasIndex(i => i.InspectionKey).IsUnique();
            e.HasIndex(i => new { i.InspectedAt, i.Id });
            e.Property(i => i.InspectedAt).HasColumnType("datetime(6)");
            e.Property(i => i.Result).HasMaxLength(20).IsRequired();
            e.Property(i => i.ImagePath).HasMaxLength(255).IsRequired();
            e.Property(i => i.ModelName).HasMaxLength(100).IsRequired();
            e.Property(i => i.ModelVersion).HasMaxLength(50).IsRequired();
            e.Property(i => i.CreatedAt).HasColumnType("datetime(6)");

            e.HasMany(i => i.Items)
                .WithOne(x => x.Inspection)
                .HasForeignKey(x => x.InspectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InspectionItem>(e =>
        {
            e.ToTable("InspectionItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.EquipmentCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
            e.HasIndex(x => new { x.InspectionId, x.EquipmentCode }).IsUnique();
        });
    }
}
