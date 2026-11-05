namespace TusaMap.Api.Services;

public sealed record MeetupVenue(
    string Id,
    string Name,
    string Type,
    string Address,
    double? Lat,
    double? Lng,
    string? OpeningHours,
    string? Website,
    string MapUrl,
    bool IsFeatured = false,
    string? PromotionLabel = null,
    DateTime? FeaturedUntilUtc = null,
    string? ImageUrl = null,
    string? GalleryUrl = null,
    string? PreviewPostUrl = null);

public interface IMeetupVenueCatalog
{
    string DataSource { get; }
    DateTime CheckedAtUtc { get; }
    IReadOnlyList<MeetupVenue> GetVenues();
}

/// <summary>Small, manually checked starter catalog. Verify details with the venue before going.</summary>
public sealed class CuratedAstanaVenueCatalog : IMeetupVenueCatalog
{
    public string DataSource => "2GIS public business listings";
    public DateTime CheckedAtUtc => new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<MeetupVenue> Venues =
    [
        // The name was requested by the app owner; exact location and current photos stay on the linked venue page until licensed assets are supplied.
        new("bourbon-street-astana", "Bourbon Street Bar", "bar", "ул. Ахмет Байтурсынулы, 23Б", 51.123091, 71.480573, null, null,
            "https://2gis.kz/astana/firm/70000001041001203", true, "Выбор TUSA", null, null,
            "https://www.instagram.com/bourbon.street.tse/", "https://www.instagram.com/reel/DdCF13PuQ2_/"),
        new("2gis-70000001052643507", "Мята", "lounge", "пр. Кабанбай батыра, 46", 51.115561, 71.413122, null, null, "https://2gis.kz/astana/firm/70000001052643507"),
        new("2gis-70000001109602205", "Afra", "bar", "ул. Гейдара Алиева, 16", 51.089861, 71.409856, null, null, "https://2gis.kz/astana/firm/70000001109602205"),
        new("astana-joy", "Joy bar&restaurant", "bar", "пр. Туран, 20", 51.141449, 71.411205, null, null, "https://2gis.kz/astana/search/Joy%20bar%26restaurant"),
        new("astana-22bar", "22 BAR", "bar", "пр. Туран, 19/2", 51.143783, 71.409247, null, null, "https://2gis.kz/astana/search/22%20BAR", GalleryUrl: "https://www.instagram.com/22.astana/"),
        new("astana-black-duck", "Black Duck", "bar", "пр. Туран, 37/9", 51.130965, 71.402213, null, null, "https://2gis.kz/astana/search/Black%20Duck", GalleryUrl: "https://www.instagram.com/blackduck_astana/"),
        new("astana-fiftyfifty", "Fiftyfifty Luckybar", "bar", "пр. Туран, 22/1", 51.139776, 71.411843, null, null, "https://2gis.kz/astana/search/Fiftyfifty%20Luckybar"),
        new("astana-blackcat", "Черный Кот", "bar", "пр. Кабанбай Батыра, 42", 51.119518, 71.413981, null, null, "https://2gis.kz/astana/search/%D0%A7%D0%B5%D1%80%D0%BD%D1%8B%D0%B9%20%D0%9A%D0%BE%D1%82", GalleryUrl: "https://www.instagram.com/blackcat_bar01/"),
        new("astana-pub-wien", "Pub Wien", "bar", "пр. Кабанбай Батыра, 2/1", 51.149060, 71.425605, null, null, "https://2gis.kz/astana/search/Pub%20Wien"),
        new("astana-dublin", "Dublin", "bar", "ул. Ілияс Омаров, 27", 51.141568, 71.370449, null, null, "https://2gis.kz/astana/search/Dublin", GalleryUrl: "https://www.instagram.com/dublin_pub_astana/"),
        new("astana-kruger", "Kruger", "bar", "ул. Акан Серы, 16", 51.188983, 71.382549, null, null, "https://2gis.kz/astana/search/Kruger"),
        new("astana-dark-side", "Dark Side", "bar", "пр. Мангилик Ел, 52/1", 51.091399, 71.426530, null, null, "https://2gis.kz/astana/search/Dark%20Side", GalleryUrl: "https://www.instagram.com/darkside.bar/"),
        new("astana-good-bar", "Good Bar", "bar", "ул. Иманова, 5", 51.163652, 71.431584, null, null, "https://2gis.kz/astana/search/Good%20Bar"),
        new("2gis-70000001106129435", "Mojo", "club", "ул. Бейімбет Майлин, 4", 51.142431, 71.466401, null, null, "https://2gis.kz/astana/firm/70000001106129435", GalleryUrl: "https://www.instagram.com/mojo.club.astana/"),
        new("2gis-70000001114702148", "Pimto", "club", "ул. Манас, 9/1", 51.156501, 71.488008, null, null, "https://2gis.kz/astana/firm/70000001114702148"),
        new("2gis-70000001060362555", "Bla Bla Bar Astana", "club", "ул. Сыганак, 60/5 · Abu Dhabi Plaza", 51.122507, 71.428920, null, null, "https://2gis.kz/astana/firm/70000001060362555", GalleryUrl: "https://www.instagram.com/blablabar.astana/"),
        new("2gis-70000001117475515", "Union", "club", "шоссе Коргалжын, 2а", 51.148856, 71.415901, null, null, "https://2gis.kz/astana/firm/70000001117475515", GalleryUrl: "https://www.instagram.com/union.astana/"),
        new("2gis-70000001100142232", "Ретро", "club", "пр. Туран, 25", 51.141087, 71.409398, null, null, "https://2gis.kz/astana/firm/70000001100142232", GalleryUrl: "https://www.instagram.com/retro_bar.astana/"),
    ];

    public IReadOnlyList<MeetupVenue> GetVenues() => Venues;
}
