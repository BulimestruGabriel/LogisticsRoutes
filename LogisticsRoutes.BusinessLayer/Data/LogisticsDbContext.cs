using LogisticsRoutes.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogisticsRoutes.BusinessLayer.Data;

public class LogisticsDbContext(DbContextOptions<LogisticsDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<RouteStop> RouteStops => Set<RouteStop>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.Property(order => order.Volume).HasPrecision(18, 3);
            entity.Property(order => order.Status).HasConversion<string>().HasColumnType("text");
        });

        modelBuilder.Entity<Vehicle>(entity =>
        {
            entity.Property(vehicle => vehicle.Capacity).HasPrecision(18, 3);
        });

        modelBuilder.Entity<Route>(entity =>
        {
            entity.HasOne(route => route.Vehicle)
                .WithMany()
                .HasForeignKey(route => route.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(route => route.Driver)
                .WithMany()
                .HasForeignKey(route => route.DriverId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(route => route.Stops)
                .WithOne(stop => stop.Route)
                .HasForeignKey(stop => stop.RouteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RouteStop>(entity =>
        {
            entity.HasOne(stop => stop.Order)
                .WithOne()
                .HasForeignKey<RouteStop>(stop => stop.OrderId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(stop => new { stop.RouteId, stop.Sequence }).IsUnique();
            entity.HasIndex(stop => stop.OrderId).IsUnique();
            entity.Property(stop => stop.DeliveryStatus).HasConversion<string>().HasColumnType("text");
        });
    }
}
