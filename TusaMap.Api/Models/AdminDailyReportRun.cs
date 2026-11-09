namespace TusaMap.Api.Models;

public class AdminDailyReportRun
{
    public string ReportDate { get; set; } = "";
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}
