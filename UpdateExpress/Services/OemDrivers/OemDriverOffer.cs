namespace UpdateExpress.Services.OemDrivers;

/// <summary>One OEM catalog row before mapping into <see cref="Models.UpdateEntry"/>.</summary>
internal sealed record OemDriverOffer(
    string Brand,
    string Id,
    string Title,
    string Category,
    string Version,
    string Priority,
    string Status,
    string DownloadUrl,
    string DetailsUrl,
    DateTimeOffset? ReleasedAt,
    string Reason);
