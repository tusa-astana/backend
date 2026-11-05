using Microsoft.EntityFrameworkCore;
using TusaMap.Api.Data;
using TusaMap.Api.Models;

namespace TusaMap.Api.Services;

public interface IUserStore
{
    AppUser Upsert(TelegramUser user);
    AppUser? Get(long telegramUserId);
    bool IsAdmin(long telegramUserId);
}

public class UserStore : IUserStore
{
    private readonly TusaMapDbContext _db;
    private readonly HashSet<long> _adminIds;

    public UserStore(TusaMapDbContext db, IConfiguration configuration)
    {
        _db = db;
        _adminIds = configuration.GetSection("Telegram:AdminUserIds").Get<long[]>()?.ToHashSet() ?? [];
    }

    public AppUser Upsert(TelegramUser user)
    {
        var existing = _db.Users.Find(user.Id);
        if (existing is null)
        {
            existing = new AppUser { TelegramUserId = user.Id, Role = _adminIds.Contains(user.Id) ? "admin" : "user" };
            _db.Users.Add(existing);
        }

        existing.FirstName = user.FirstName;
        existing.LastName = user.LastName;
        existing.Username = user.Username;
        existing.PhotoUrl = Uri.TryCreate(user.PhotoUrl, UriKind.Absolute, out var photoUri) && photoUri.Scheme == Uri.UriSchemeHttps
            ? user.PhotoUrl
            : null;
        existing.LastSeenAt = DateTime.UtcNow;
        if (_adminIds.Contains(user.Id)) existing.Role = "admin";
        _db.SaveChanges();
        return existing;
    }

    public AppUser? Get(long telegramUserId) => _db.Users.AsNoTracking().FirstOrDefault(x => x.TelegramUserId == telegramUserId);
    public bool IsAdmin(long telegramUserId) => _adminIds.Contains(telegramUserId) || Get(telegramUserId)?.Role == "admin";
}