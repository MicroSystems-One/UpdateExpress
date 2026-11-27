namespace UpdateExpress.Services;

/// <summary>OK-only information notice (title, lead line, remainder) for custom chrome windows.</summary>
internal sealed record InformationNotice(string Title, string Lead, string Detail)
{
    internal string Body => string.IsNullOrEmpty(Detail) ? Lead : Lead + "\n\n" + Detail;
}