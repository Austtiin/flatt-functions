#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using flatt_functions.Models;

namespace flatt_functions.Data
{
    public class InventoryContext : DbContext
    {
        public InventoryContext(DbContextOptions<InventoryContext> options) : base(options)
        {
            ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        }

        // Fallback for tooling/design-time scenarios if DI didn't configure the provider
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var cs = Environment.GetEnvironmentVariable("SqlConnectionString");
                if (!string.IsNullOrWhiteSpace(cs))
                {
                    optionsBuilder.UseSqlServer(cs, sql =>
                    {
                        sql.EnableRetryOnFailure();
                    });
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Unit
            modelBuilder.Entity<Unit>(entity =>
            {
                entity.ToTable("Units");
                entity.HasKey(u => u.UnitID);

                // Map Status -> UnitStatus (in case attribute is missing)
                entity.Property(u => u.Status).HasColumnName("UnitStatus");

                entity.Property(u => u.Price).HasColumnType("decimal(18,2)");

                // Relationships
                entity.HasOne(u => u.UnitType)
                      .WithMany(t => t.Units)
                      .HasForeignKey(u => u.TypeID)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasMany(u => u.UnitFeatures)
                      .WithOne(f => f.Unit!)
                      .HasForeignKey(f => f.UnitID)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(u => u.UnitImages)
                      .WithOne(i => i.Unit!)
                      .HasForeignKey(i => i.UnitID)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // UnitType
            modelBuilder.Entity<UnitType>(entity =>
            {
                entity.ToTable("UnitTypes");
                entity.HasKey(t => t.TypeID);
            });

            // UnitFeature
            modelBuilder.Entity<UnitFeature>(UnitFeatureConfig);

            // UnitImage
            modelBuilder.Entity<UnitImage>(UnitImageConfig);
        }

        private static void UnitFeatureConfig(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<UnitFeature> entity)
        {
            entity.ToTable("UnitFeatures");
            entity.HasKey(f => f.FeatureID);
            entity.Property(f => f.FeatureName).HasMaxLength(256);
        }

        private static void UnitImageConfig(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<UnitImage> entity)
        {
            entity.ToTable("UnitImages");
            entity.HasKey(i => i.ImageID);
            entity.Property(i => i.ImageURL).HasMaxLength(1024);
        }

        public DbSet<Unit> Units => Set<Unit>();
        public DbSet<UnitType> UnitTypes => Set<UnitType>();
        public DbSet<UnitFeature> UnitFeatures => Set<UnitFeature>();
        public DbSet<UnitImage> UnitImages => Set<UnitImage>();
    }
}