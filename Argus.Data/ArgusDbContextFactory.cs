using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Argus.Data;

public class ArgusDbContextFactory : IDesignTimeDbContextFactory<ArgusDbContext>
{
    public ArgusDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArgusDbContext>();
        optionsBuilder.UseSqlite("Data Source=design_time_argus.db");
        return new ArgusDbContext(optionsBuilder.Options);
    }
}
