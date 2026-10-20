using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;

namespace AiCtrl.Server.Security;

public interface IGeoIpLookup
{
    string? GetCountry(IPAddress address);
}

public sealed class MaxMindGeoIpLookup : IGeoIpLookup, IDisposable
{
    private readonly DatabaseReader? _reader;

    public MaxMindGeoIpLookup(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return;
        }

        _reader = new DatabaseReader(databasePath);
    }

    public string? GetCountry(IPAddress address)
    {
        if (_reader is null)
        {
            return null;
        }

        try
        {
            return _reader.Country(address).Country.IsoCode;
        }
        catch (AddressNotFoundException)
        {
            return null;
        }
        catch (InvalidDatabaseException)
        {
            return null;
        }
    }

    public void Dispose() => _reader?.Dispose();
}
