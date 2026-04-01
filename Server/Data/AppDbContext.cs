using ClassroomController.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ClassroomController.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Device> Devices { get; set; } = null!;
    public DbSet<Rule> Rules { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(d => d.MacAddress);
            entity.Property(d => d.Status).HasDefaultValue("Offline");
            entity.Property(d => d.MacAddress).IsRequired();
            entity.Property(d => d.IpAddress).IsRequired();
            entity.Property(d => d.Hostname).IsRequired();
        });

        modelBuilder.Entity<Rule>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Url).IsRequired();
        });
    }
}
