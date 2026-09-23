using System.Text;
using System.Text.Json.Nodes;
using TanssSystemCapture.Client.Services;
using TnsApiImport;

var tests = new (string Name, Action Test)[]
{
    ("Server-Boolean", TestServerBoolean),
    ("Datumsumwandlung und Garantieobjekt", TestGuaranteePayload),
    ("Fehlende Garantiedaten", TestMissingGuarantee),
    ("Ungültiges Datum", TestInvalidDate),
    ("Seriennummern-Platzhalter", TestSerialPlaceholder),
    ("Gerätemetadaten und Eigengerät", TestDeviceMetadata),
    ("Kompatibilität mit Client 0.15.0", TestLegacyArticleNumber),
    ("Widersprüchliche Artikelnummern ablehnen", TestConflictingArticleNumbers),
    ("Betriebssystemfilter", TestOperatingSystemFilter),
    ("Ungültige Katalogeinträge überspringen", TestInvalidCatalogEntry),
    ("Vorhandenes System ergänzen", TestSupplementPlanner),
    ("Vorhandenes System überschreiben", TestOverwritePlanner),
    ("Inaktiven Hostnamen-Vorgänger bei Aktualisierung ignorieren", TestInactivePredecessorPolicy),
    ("Aktiven Hostnamen-Dublettentreffer blockieren", TestActiveDuplicatePolicy),
    ("Nicht unterstützten Kopiermodus ablehnen", TestCopyModeRemoved),
    ("TOTP RFC-6238-Testvektoren", TestTotpRfcVectors),
    ("TOTP Base32 und sechsstellige Prüfung", TestTotpVerification),
    ("Passkey-Eingabegrenzen", TestSecurityKeyInputPolicy),
    ("Passkey-Registrierungscode-Hash", TestSecurityKeyEnrollmentHash),
    ("Passkey-Origin-Prüfung", TestSecurityKeyOriginValidation),
    ("Dynamische Client-Serveradresse", TestDynamicClientServerAddress),
    ("TANSS-Gerätelink-Template", TestTanssDeviceUrlTemplate),
    ("SAP-Freigaben aus Konfiguration", TestSapConfigurationAllowList),
    ("Ungültige Wortmann-Antwort", TestInvalidWortmannResponse),
    ("Mehrdeutige Wortmann-Seriennummern ablehnen", TestAmbiguousWortmannResponse),
    ("Gültige Wortmann-Antwort", TestValidWortmannResponse),
    ("Wortmann-Geräteposition als Hersteller-Nr.", TestWortmannDeviceArticleSelection)
};

var failed = 0;

foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"OK: {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FEHLER: {name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static DeviceTransferPreviewRequest NewRequest(
    bool server = false,
    string? serialNumber = null,
    string? purchaseDate = null,
    int? guaranteeMonth = null,
    string? guaranteeExpire = null,
    string? guaranteeRemark = null) =>
    new(
        Name: "TEST-PC",
        Model: "Testmodell",
        SerialNumber: serialNumber,
        TeamviewerId: null,
        Remark: null,
        InternalRemark: null,
        Server: server,
        PurchaseDate: purchaseDate,
        GuaranteeMonth: guaranteeMonth,
        GuaranteeExpire: guaranteeExpire,
        GuaranteeRemark: guaranteeRemark,
        IsVirtualMachine: false,
        HostId: null,
        NetworkAdapters: Array.Empty<DeviceTransferNetworkAdapter>());

static void TestServerBoolean()
{
    var result = DeviceTransferPreviewBuilder.Build(439, NewRequest(server: true));
    Equal(true, result.RequestBody["server"]!.GetValue<bool>());
}

static void TestGuaranteePayload()
{
    var result = DeviceTransferPreviewBuilder.Build(
        439,
        NewRequest(
            purchaseDate: "140926",
            guaranteeMonth: 60,
            guaranteeExpire: "140929",
            guaranteeRemark: "Vor Ort Service"));
    var guarantee = result.RequestBody["guarantee"]!.AsObject();

    Equal(1789336800L, result.RequestBody["date"]!.GetValue<long>());
    Equal(1789423140L, guarantee["purchaseDate"]!.GetValue<long>());
    Equal(60, guarantee["guaranteeMonth"]!.GetValue<int>());
    Equal(1884117540L, guarantee["guaranteeExpire"]!.GetValue<long>());
    Equal("Vor Ort Service", guarantee["remark"]!.GetValue<string>());
    Equal(false, guarantee.ContainsKey("linkTypeId"));
    Equal(false, guarantee.ContainsKey("linkId"));
    Equal(false, guarantee.ContainsKey("warrantyMonth"));
    Equal(false, guarantee.ContainsKey("warrantyExpire"));
}

static void TestMissingGuarantee()
{
    var result = DeviceTransferPreviewBuilder.Build(439, NewRequest());
    Equal(false, result.RequestBody.ContainsKey("guarantee"));
    Equal(false, result.RequestBody.ContainsKey("date"));
}

static void TestInvalidDate()
{
    Throws<TransferPreviewValidationException>(() =>
        DeviceTransferPreviewBuilder.Build(
            439,
            NewRequest(purchaseDate: "310226")));
}

static void TestSerialPlaceholder()
{
    Throws<TransferPreviewValidationException>(() =>
        DeviceTransferPreviewBuilder.Build(
            439,
            NewRequest(serialNumber: "To be filled by O.E.M.")));
}

static void TestDeviceMetadata()
{
    var request = NewRequest() with
    {
        ManufacturerId = 70,
        OsId = 17,
        ArticleNumber = "ERP-4711",
        ManufacturerNumber = "1470824"
    };
    var result = DeviceTransferPreviewBuilder.Build(439, request);

    Equal(70L, result.RequestBody["manufacturerId"]!.GetValue<long>());
    Equal(17L, result.RequestBody["osId"]!.GetValue<long>());
    Equal("ERP-4711", result.RequestBody["articleNumber"]!.GetValue<string>());
    Equal("1470824", result.RequestBody["manufacturerNumber"]!.GetValue<string>());
    Equal(false, result.RequestBody.ContainsKey("billingNumber"));
    Equal("OWN", result.RequestBody["ownageType"]!.GetValue<string>());
    Equal(
        true,
        result.MappedFields.Any(mapping =>
            mapping.SourceField == "SAP-Artikel-Nr." &&
            mapping.TanssField == "articleNumber"));
    Equal(
        true,
        result.MappedFields.Any(mapping =>
            mapping.SourceField == "Wortmann-Hersteller-Nr." &&
            mapping.TanssField == "manufacturerNumber"));
}

static void TestLegacyArticleNumber()
{
    var result = DeviceTransferPreviewBuilder.Build(
        439,
        NewRequest() with
        {
            BillingNumber = "ERP-4711"
        });

    Equal("ERP-4711", result.RequestBody["articleNumber"]!.GetValue<string>());
    Equal(false, result.RequestBody.ContainsKey("billingNumber"));
}

static void TestConflictingArticleNumbers()
{
    Throws<TransferPreviewValidationException>(() =>
        DeviceTransferPreviewBuilder.Build(
            439,
            NewRequest() with
            {
                ArticleNumber = "ERP-4711",
                BillingNumber = "ERP-9999"
            }));
}

static void TestOperatingSystemFilter()
{
    Equal(true, DeviceCatalogPolicy.IsAllowedOperatingSystem("Windows 10"));
    Equal(true, DeviceCatalogPolicy.IsAllowedOperatingSystem("Windows Server 2016 Standard"));
    Equal(true, DeviceCatalogPolicy.IsAllowedOperatingSystem("Debian 13"));
    Equal(true, DeviceCatalogPolicy.IsAllowedOperatingSystem("MAC OSX 10.11 El Capitan"));
    Equal(false, DeviceCatalogPolicy.IsAllowedOperatingSystem("Windows 8.1"));
    Equal(false, DeviceCatalogPolicy.IsAllowedOperatingSystem("Windows Server 2012 R2"));
}

static void TestInvalidCatalogEntry()
{
    var validEntry = JsonNode.Parse("""{"id":13,"name":"Windows 11 Pro"}""")!
        .AsObject();
    var emptyNameEntry = JsonNode.Parse("""{"id":127,"name":""}""")!
        .AsObject();
    var missingIdEntry = JsonNode.Parse("""{"name":"Windows 11 Pro"}""")!
        .AsObject();

    Equal(true, DeviceCatalogParser.TryReadEntry(validEntry, out var id, out var name));
    Equal(13L, id);
    Equal("Windows 11 Pro", name);
    Equal(false, DeviceCatalogParser.TryReadEntry(emptyNameEntry, out _, out _));
    Equal(false, DeviceCatalogParser.TryReadEntry(missingIdEntry, out _, out _));
}

static void TestSupplementPlanner()
{
    var selected = DeviceTransferPreviewBuilder.Build(
        439,
        NewRequest(server: true) with
        {
            ManufacturerId = 70,
            ArticleNumber = "ERP-4711",
            ManufacturerNumber = "1470824"
        }).RequestBody;
    var existing = JsonNode.Parse(
        """
        {
          "id": 359,
          "companyId": 439,
          "name": "BESTAND",
          "model": "",
          "server": false,
          "manufacturerId": 0,
          "articleNumber": "ALT",
          "ownageType": "FOREIGN"
        }
        """)!.AsObject();

    var update = DeviceWritePlanner.BuildRequestBody(
        selected,
        existing,
        DeviceWriteMode.Supplement);

    Equal("OWN", update["ownageType"]!.GetValue<string>());
    Equal(true, update.ContainsKey("model"));
    Equal(true, update.ContainsKey("manufacturerId"));
    Equal("1470824", update["manufacturerNumber"]!.GetValue<string>());
    Equal(false, update.ContainsKey("name"));
    Equal(false, update.ContainsKey("server"));
    Equal(false, update.ContainsKey("articleNumber"));
    Equal(false, update.ContainsKey("companyId"));
}

static void TestOverwritePlanner()
{
    var selected = DeviceTransferPreviewBuilder.Build(
        439,
        NewRequest(server: true) with
        {
            ManufacturerId = 70,
            OsId = 17,
            ArticleNumber = "ERP-4711",
            ManufacturerNumber = "1470824"
        }).RequestBody;
    var existing = JsonNode.Parse(
        """{"companyId":439,"name":"ALT","model":"ALT","ownageType":"FOREIGN"}""")!
        .AsObject();

    var update = DeviceWritePlanner.BuildRequestBody(
        selected,
        existing,
        DeviceWriteMode.Overwrite);

    Equal("TEST-PC", update["name"]!.GetValue<string>());
    Equal("Testmodell", update["model"]!.GetValue<string>());
    Equal(true, update["server"]!.GetValue<bool>());
    Equal("OWN", update["ownageType"]!.GetValue<string>());
    Equal("ERP-4711", update["articleNumber"]!.GetValue<string>());
    Equal("1470824", update["manufacturerNumber"]!.GetValue<string>());
    Equal(false, update.ContainsKey("billingNumber"));
    Equal(false, update.ContainsKey("companyId"));
    Equal(false, update.ContainsKey("active"));

    var createHash = DeviceWritePlanner.CalculatePreviewSha256(
        DeviceWriteMode.Create,
        null,
        selected);
    var overwriteHash = DeviceWritePlanner.CalculatePreviewSha256(
        DeviceWriteMode.Overwrite,
        359,
        update);
    Equal(false, string.Equals(createHash, overwriteHash, StringComparison.Ordinal));
}

static void TestCopyModeRemoved()
{
    Throws<TransferPreviewValidationException>(() =>
        DeviceWritePlanner.ParseMode("COPY"));
}

static void TestInactivePredecessorPolicy()
{
    Equal(
        DeviceNameMatchDisposition.IgnoreInactiveHistoricalEntry,
        DeviceNameConflictPolicy.Classify(
            DeviceWriteMode.Overwrite,
            targetDeviceId: 365,
            matchId: 358,
            active: false));
    Equal(
        DeviceNameMatchDisposition.IgnoreTarget,
        DeviceNameConflictPolicy.Classify(
            DeviceWriteMode.Overwrite,
            targetDeviceId: 365,
            matchId: 365,
            active: true));
}

static void TestActiveDuplicatePolicy()
{
    Equal(
        DeviceNameMatchDisposition.Block,
        DeviceNameConflictPolicy.Classify(
            DeviceWriteMode.Overwrite,
            targetDeviceId: 365,
            matchId: 358,
            active: true));
    Equal(
        DeviceNameMatchDisposition.Block,
        DeviceNameConflictPolicy.Classify(
            DeviceWriteMode.Overwrite,
            targetDeviceId: 365,
            matchId: 358,
            active: null));
    Equal(
        DeviceNameMatchDisposition.Block,
        DeviceNameConflictPolicy.Classify(
            DeviceWriteMode.Create,
            targetDeviceId: null,
            matchId: 358,
            active: false));
}

static void TestTotpRfcVectors()
{
    var secret = Encoding.ASCII.GetBytes("12345678901234567890");
    var vectors = new (long Timestamp, string Code)[]
    {
        (59, "94287082"),
        (1_111_111_109, "07081804"),
        (1_111_111_111, "14050471"),
        (1_234_567_890, "89005924"),
        (2_000_000_000, "69279037"),
        (20_000_000_000, "65353130")
    };

    foreach (var vector in vectors)
    {
        var counter = vector.Timestamp / 30;
        Equal(vector.Code, TotpAlgorithm.GenerateCode(secret, counter, 8));
    }
}

static void TestTotpVerification()
{
    const string encodedSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var secret = Base32Encoding.Decode(encodedSecret);

    if (!secret.SequenceEqual(Encoding.ASCII.GetBytes("12345678901234567890")))
    {
        throw new InvalidOperationException("Der Base32-Schlüssel wurde nicht korrekt dekodiert.");
    }

    var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_234_567_890);
    var counter = timestamp.ToUnixTimeSeconds() / 30;
    var code = TotpAlgorithm.GenerateCode(secret, counter, 6);

    Equal(true, TotpAlgorithm.TryVerify(secret, code, timestamp, out var matchedCounter));
    Equal(counter, matchedCounter);
    Equal(false, TotpAlgorithm.TryVerify(secret, "12345A", timestamp, out _));
}

static void TestSecurityKeyInputPolicy()
{
    Equal(true, SecurityKeyPolicy.IsValidLabel("Passkey 1"));
    Equal(false, SecurityKeyPolicy.IsValidLabel(string.Empty));
    Equal(false, SecurityKeyPolicy.IsValidLabel("Key\nManipulation"));
    Equal(false, SecurityKeyPolicy.IsValidLabel(new string('x', 81)));
    Equal(true, SecurityKeyPolicy.IsValidTransactionId(new string('A', 43)));
    Equal(false, SecurityKeyPolicy.IsValidTransactionId("zu-kurz"));
}

static void TestSecurityKeyEnrollmentHash()
{
    Equal(
        "3FC9116F7BC25B4AF84CE4F1947ACDCF186279424344D94CAE7AB27A542A323F",
        SecurityKeyPolicy.ComputeSha256Ascii("test-registration-code"));
}

static void TestSecurityKeyOriginValidation()
{
    Equal(
        true,
        SecurityKeyPolicy.IsValidOrigin(
            "https://import.example.org",
            "import.example.org"));
    Equal(
        false,
        SecurityKeyPolicy.IsValidOrigin(
            "https://fremd.example",
            "import.example.org"));
    Equal(
        false,
        SecurityKeyPolicy.IsValidOrigin(
            "https://import.example.org/anmeldung",
            "import.example.org"));
}

static void TestDynamicClientServerAddress()
{
    var settings = ClientSettings.Create(" https://import.example.org:45001/api ");
    Equal("https://import.example.org:45001/api/", settings.ImportApiBaseUri.AbsoluteUri);

    Throws<ClientConfigurationException>(() =>
        ClientSettings.Create("http://import.example.org"));
    Throws<ClientConfigurationException>(() =>
        ClientSettings.Create("https://user:secret@import.example.org"));
    Throws<ClientConfigurationException>(() =>
        ClientSettings.Create("https://import.example.org/?token=secret"));
}

static void TestTanssDeviceUrlTemplate()
{
    var validator = new TanssOptionsValidator();
    var valid = validator.Validate(
        null,
        new TanssOptions
        {
            BaseUrl = "https://tanss.example.org/backend",
            DeviceWebUrlTemplate = "https://tanss.example.org/devices/{deviceId}",
            ErpTokenFile = "/run/secrets/erp_token",
            DeviceManagementTokenFile = "/run/secrets/device_management_token"
        });
    Equal(false, valid.Failed);

    var invalid = validator.Validate(
        null,
        new TanssOptions
        {
            BaseUrl = "https://tanss.example.org/backend",
            DeviceWebUrlTemplate = "http://tanss.example.org/devices/{deviceId}",
            ErpTokenFile = "/run/secrets/erp_token",
            DeviceManagementTokenFile = "/run/secrets/device_management_token"
        });
    Equal(true, invalid.Failed);
}

static void TestSapConfigurationAllowList()
{
    var validator = new SapOptionsValidator();
    var valid = validator.Validate(
        null,
        new SapOptions
        {
            Enabled = true,
            Server = "sql.example.org",
            Database = "SAP_DATABASE",
            AllowedDatabases = ["SAP_DATABASE"],
            UserName = "sap_readonly",
            RequiredUserName = "sap_readonly",
            PasswordFile = "/run/secrets/sap_sql_password"
        });
    Equal(false, valid.Failed);

    var invalid = validator.Validate(
        null,
        new SapOptions
        {
            Enabled = true,
            Server = "sql.example.org",
            Database = "UNAPPROVED_DATABASE",
            AllowedDatabases = ["SAP_DATABASE"],
            UserName = "sap_readonly",
            RequiredUserName = "sap_readonly",
            PasswordFile = "/run/secrets/sap_sql_password"
        });
    Equal(true, invalid.Failed);
}

static void TestInvalidWortmannResponse()
{
    var result = WortmannWarrantyClient.ParseSearchResult(
        "<html><body>Keine Daten</body></html>",
        "SERIAL-EXAMPLE-001");
    Equal<WortmannWarrantyResult?>(null, result);
}

static void TestValidWortmannResponse()
{
    const string html = """
        <table>
          <tr><td>Artikelnr.</td><td>1220816</td></tr>
          <tr><td>Beschreibung</td><td>TERRA MOBILE</td></tr>
          <tr><td>Seriennummer</td><td>SERIAL-EXAMPLE-001</td></tr>
          <tr><td>Servicebeginn</td><td>07.04.2025</td></tr>
          <tr><td>Serviceende</td><td>07.04.2028</td></tr>
          <tr><td>Servicecode</td><td>GB36</td></tr>
          <tr><td>Servicebeschreibung</td><td>TERRA Mobile Pickup + Expressreparatur 36 Monate</td></tr>
        </table>
        """;
    var result = WortmannWarrantyClient.ParseSearchResult(html, "SERIAL-EXAMPLE-001")!;

    Equal(new DateOnly(2025, 4, 7), result.ServiceStart);
    Equal(new DateOnly(2028, 4, 7), result.ServiceEnd);
    Equal(36, result.GuaranteeMonth);
    Equal("GB36", result.ServiceCode);
    Equal("1220816", result.ArticleNumber);
}

static void TestAmbiguousWortmannResponse()
{
    const string html = """
        <table>
          <tr><td>Seriennummer</td><td>SERIAL-EXAMPLE-001</td></tr>
          <tr><td>Seriennummer</td><td>SERIAL-EXAMPLE-002</td></tr>
        </table>
        """;

    Throws<WortmannWarrantyException>(() =>
        WortmannWarrantyClient.ParseSearchResult(html, "SERIAL-EXAMPLE-001"));
}

static void TestWortmannDeviceArticleSelection()
{
    const string html = """
        <table>
          <tr><td>Artikelnr.</td><td>1220816</td></tr>
          <tr><td>Beschreibung</td><td>TERRA MOBILE GAMER ELITE 3 Ultra 7-155H W11P</td></tr>
          <tr><td>Seriennummer</td><td>SERIAL-EXAMPLE-001</td></tr>
          <tr><td>Servicebeginn</td><td>07.04.2025</td></tr>
          <tr><td>Serviceende</td><td>07.04.2028</td></tr>
          <tr><td>Servicecode</td><td>GB36</td></tr>
          <tr><td>Servicebeschreibung</td><td>TERRA Mobile Pickup + Expressreparatur 36 Monate</td></tr>
        </table>
        <table>
          <tr><th>Artikelnr.</th><th>Beschreibung</th></tr>
          <tr><td>9900002</td><td>Gebühren und Abgaben (DE/AT)</td></tr>
          <tr><td>152</td><td>Montage - Assemblierung NB/Sonder/PAD/Nettop</td></tr>
          <tr><td>1470824</td><td>NB MOBILE GAMER ELITE 3 16 Zoll - CPU Intel - RAM 64GB - RTX4060</td></tr>
        </table>
        """;

    var result = WortmannWarrantyClient.ParseSearchResult(html, "SERIAL-EXAMPLE-001")!;

    Equal("1470824", result.ArticleNumber);
    Equal("TERRA MOBILE GAMER ELITE 3 Ultra 7-155H W11P", result.ProductDescription);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Erwartet: {expected}; erhalten: {actual}");
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Erwartete Ausnahme {typeof(TException).Name} wurde nicht ausgelöst.");
}
