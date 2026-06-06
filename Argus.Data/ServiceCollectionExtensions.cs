using Argus.Core.Services;
using Argus.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Argus.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddArgusData(this IServiceCollection services, string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        services.AddDbContextFactory<ArgusDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<ArgusDatabaseInitializer>();
        services.AddSingleton<IGraphService, GraphService>();
        services.AddSingleton<ITagService, TagService>();
        services.AddSingleton<IGraphExchangeService, GraphExchangeService>();
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IMemoryService, LocalMemoryService>();
        services.AddSingleton<IMemoryProvider>(provider => (LocalMemoryService)provider.GetRequiredService<IMemoryService>());
        services.AddSingleton<ISettingsService, SettingsService>();
        return services;
    }
}
