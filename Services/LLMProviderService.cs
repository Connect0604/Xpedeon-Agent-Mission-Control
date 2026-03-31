using Microsoft.EntityFrameworkCore;
using XpedeonAgentMissionControl.Data;
using XpedeonAgentMissionControl.Models;

namespace XpedeonAgentMissionControl.Services;

public class LLMProviderService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public LLMProviderService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<LLMProvider>> GetAllAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.LLMProviders.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<LLMProvider?> GetByIdAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        return await db.LLMProviders.FindAsync(id);
    }

    public async Task<LLMProvider?> GetDefaultAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.LLMProviders.FirstOrDefaultAsync(p => p.IsDefault && p.IsEnabled);
    }

    public async Task<LLMProvider> CreateAsync(LLMProvider provider)
    {
        await using var db = _factory.CreateDbContext();

        if (provider.IsDefault)
        {
            // Clear existing defaults
            var existing = await db.LLMProviders.Where(p => p.IsDefault).ToListAsync();
            existing.ForEach(p => p.IsDefault = false);
        }

        provider.Id = Guid.NewGuid().ToString();
        provider.CreatedAt = DateTime.UtcNow;
        db.LLMProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    public async Task<LLMProvider> UpdateAsync(LLMProvider provider)
    {
        await using var db = _factory.CreateDbContext();

        if (provider.IsDefault)
        {
            var existing = await db.LLMProviders.Where(p => p.IsDefault && p.Id != provider.Id).ToListAsync();
            existing.ForEach(p => p.IsDefault = false);
        }

        db.LLMProviders.Update(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    public async Task DeleteAsync(string id)
    {
        await using var db = _factory.CreateDbContext();
        var provider = await db.LLMProviders.FindAsync(id);
        if (provider != null)
        {
            db.LLMProviders.Remove(provider);
            await db.SaveChangesAsync();
        }
    }

    public async Task<bool> TestConnectionAsync(LLMProvider provider)
    {
        // Basic connectivity check
        try
        {
            if (provider.Type == LLMProviderType.Ollama)
            {
                var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint)
                    ? "http://localhost:11434"
                    : provider.Endpoint.TrimEnd('/');
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await http.GetAsync($"{endpoint}/api/tags");
                return response.IsSuccessStatusCode;
            }
            return !string.IsNullOrWhiteSpace(provider.ApiKey);
        }
        catch
        {
            return false;
        }
    }
}
