using Microsoft.EntityFrameworkCore;
using Domains.Entities;

namespace Infrastructures.Persistence;

public static class DbSeeder
{
    public static async Task SeedAsync(PosDbContext db)
    {
        Console.WriteLine("api-pos DbSeeder: Checking if location seed is needed...");

        if (!await db.Locations.AnyAsync())
        {
            Console.WriteLine("api-pos DbSeeder: Seeding locations...");
            
            var locations = new List<Location>
            {
                new() { LocationName = "Antipolo Store Branch", LocationType = "Store", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "Taytay Store Branch", LocationType = "Store", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "SM City Taytay Bazaar", LocationType = "Bazaar", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "SM Center Angono Bazaar", LocationType = "Bazaar", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "SM City Fairview Bazaar", LocationType = "Bazaar", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "Ayala Malls Arca South Bazaar", LocationType = "Bazaar", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "Estancia Capitol Commons Bazaar", LocationType = "Bazaar", IsActive = true, CreatedAt = DateTime.UtcNow },
                new() { LocationName = "TriNoma Bazaar", LocationType = "Bazaar", IsActive = true, CreatedAt = DateTime.UtcNow }
            };

            await db.Locations.AddRangeAsync(locations);
            await db.SaveChangesAsync();
            
            Console.WriteLine("api-pos DbSeeder: Locations seeded successfully.");
        }
        else
        {
            Console.WriteLine("api-pos DbSeeder: Locations already seeded.");
        }
    }
}
