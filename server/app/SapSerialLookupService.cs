using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace TnsApiImport;

public sealed record SapSerialLookupResult(
    string SerialNumber,
    IReadOnlyList<string> ItemCodes)
{
    public int MatchCount => ItemCodes.Count;

    public string Resolution => ItemCodes.Count switch
    {
        0 => "NONE",
        1 => "SINGLE",
        _ => "MULTIPLE"
    };

    public string? ItemCode =>
        ItemCodes.Count == 1
            ? ItemCodes[0]
            : null;
}

public sealed class SapLookupConfigurationException : Exception
{
    public SapLookupConfigurationException(string message)
        : base(message)
    {
    }

    public SapLookupConfigurationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SapSerialLookupService
{
    private const int MaximumSerialNumberLength = 36;

    private readonly SapOptions _options;

    public SapSerialLookupService(IOptions<SapOptions> options)
    {
        _options = options.Value;
    }

    public async Task<SapSerialLookupResult> FindBySerialNumberAsync(
        string serialNumber,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new SapLookupConfigurationException(
                "Die SAP-Seriennummernsuche ist nicht aktiviert.");
        }

        var normalizedSerialNumber = serialNumber.Trim();

        if (normalizedSerialNumber.Length is < 1 or > MaximumSerialNumberLength)
        {
            throw new ArgumentException(
                $"Die Seriennummer muss zwischen 1 und " +
                $"{MaximumSerialNumberLength} Zeichen lang sein.",
                nameof(serialNumber));
        }

        if (normalizedSerialNumber.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Die Seriennummer enthält unzulässige Steuerzeichen.",
                nameof(serialNumber));
        }

        var password = ReadPassword();

        var connectionStringBuilder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{_options.Server},{_options.Port}",
            InitialCatalog = _options.Database,
            UserID = _options.UserName,
            Password = password,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = _options.TrustServerCertificate,
            ConnectTimeout = _options.ConnectionTimeoutSeconds,
            ApplicationName = "TANSS Device Import API",
            PersistSecurityInfo = false,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 10,
            MultipleActiveResultSets = false
        };

        await using var connection =
            new SqlConnection(connectionStringBuilder.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        const string commandText = """
            SELECT DISTINCT
                [ItemCode]
            FROM [dbo].[OSRN]
            WHERE [DistNumber] = @SerialNumber
            ORDER BY [ItemCode];
            """;

        await using var command =
            new SqlCommand(commandText, connection)
            {
                CommandType = CommandType.Text,
                CommandTimeout = _options.CommandTimeoutSeconds
            };

        command.Parameters.Add(
            new SqlParameter(
                "@SerialNumber",
                SqlDbType.NVarChar,
                MaximumSerialNumberLength)
            {
                Value = normalizedSerialNumber
            });

        var itemCodes = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess |
            CommandBehavior.SingleResult,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                var itemCode = reader.GetString(0).Trim();

                if (itemCode.Length > 0)
                {
                    itemCodes.Add(itemCode);
                }
            }
        }

        return new SapSerialLookupResult(
            normalizedSerialNumber,
            itemCodes);
    }

    private string ReadPassword()
    {
        try
        {
            var password = File
                .ReadAllText(_options.PasswordFile)
                .TrimEnd('\r', '\n');

            if (password.Length == 0)
            {
                throw new SapLookupConfigurationException(
                    "Die SAP-Kennwortdatei ist leer.");
            }

            return password;
        }
        catch (SapLookupConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SapLookupConfigurationException(
                "Die SAP-Kennwortdatei ist nicht lesbar.",
                exception);
        }
    }
}
