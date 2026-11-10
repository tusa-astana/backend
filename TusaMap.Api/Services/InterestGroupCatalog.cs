namespace TusaMap.Api.Services;

public sealed record InterestGroup(string Id, string Name, string Description, string Emoji);

public interface IInterestGroupCatalog
{
    IReadOnlyList<InterestGroup> GetGroups();
    InterestGroup? Find(string id);
}

public sealed class CuratedInterestGroupCatalog : IInterestGroupCatalog
{
    private static readonly IReadOnlyList<InterestGroup> Groups =
    [
        new("board-games", "Настольные игры", "Собраться за настолками и познакомиться за партией.", "♟"),
        new("running", "Бег и прогулки", "Лёгкие пробежки и прогулки по городу.", "🏃"),
        new("padel", "Падел", "Искать компанию для игры и встречаться на корте. Бронирование и оплата — у площадки.", "🎾"),
        new("equestrian", "Конный спорт", "Находить занятия и открытые встречи с конными клубами. Условия уточняйте у организатора.", "🐎"),
        new("live-music", "Живая музыка", "Концерты, джемы и музыкальные вечера.", "♫"),
        new("performing-arts", "Театр, опера и балет", "Спектакли и сценические события; даты и условия участия уточняйте у организатора.", "🎭"),
        new("cinema", "Кино", "Совместные походы в кино и обсуждения фильмов.", "▣"),
        new("books", "Книги", "Книжные встречи и спокойные разговоры.", "▤"),
        new("coffee", "Кофе и разговоры", "Неформальные встречи за чашкой кофе.", "☕"),
        new("photography", "Фото-прогулки", "Искать интересные места и снимать город.", "◎"),
        new("hiking", "Походы и природа", "Планировать активный отдых и выезды на природу.", "⌁"),
        new("gaming", "Игры", "Обсуждать игры и встречаться офлайн.", "⌘"),
        new("language-exchange", "Языковой обмен", "Практиковать языки в дружеской компании.", "文"),
        new("food", "Еда и гастро-прогулки", "Пробовать новые места и кухни города.", "◇"),
        new("technology", "Технологии и стартапы", "Обмениваться идеями и опытом.", "⌬"),
    ];

    public IReadOnlyList<InterestGroup> GetGroups() => Groups;
    public InterestGroup? Find(string id) => Groups.FirstOrDefault(group => group.Id == id);
}
