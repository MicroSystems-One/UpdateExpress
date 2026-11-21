namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// OEM support-catalog search. Lenovo is v1; Dell/HP/ASUS plug in behind the same shape.
/// </summary>
internal interface IOemDriverCatalog
{
    /// <summary>Brand label shown in Source (e.g. Lenovo).</summary>
    string Brand { get; }

    /// <summary>True when this catalog can search for the given manufacturer string.</summary>
    bool Matches(string manufacturer);

    /// <summary>
    /// Query the public OEM catalog. Never throws for expected network/parse misses —
    /// returns an empty list and optionally reports via <paramref name="report"/>.
    /// </summary>
    Task<IReadOnlyList<OemDriverOffer>> SearchAsync(
        MachineIdentityReader.Identity identity,
        Action<string>? report = null,
        CancellationToken cancellationToken = default);
}
