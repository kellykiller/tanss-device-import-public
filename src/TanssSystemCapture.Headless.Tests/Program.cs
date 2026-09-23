using TanssSystemCapture.Headless;

var tests = new (string Name, Action Run)[]
{
    ("PRETTY_NAME aus os-release", TestPrettyName),
    ("Fallback aus NAME und VERSION_ID", TestFallbackName),
    ("Escaping in os-release", TestEscaping)
};
var failures = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS: " + test.Name);
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void TestPrettyName()
{
    const string input = "NAME=Ubuntu\nPRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\n";
    AssertEqual("Ubuntu 24.04.3 LTS", LinuxSystemCaptureService.ParseOsRelease(input));
}

static void TestFallbackName()
{
    const string input = "NAME=Debian\nVERSION_ID=\"13\"\n";
    AssertEqual("Debian 13", LinuxSystemCaptureService.ParseOsRelease(input));
}

static void TestEscaping()
{
    const string input = "PRETTY_NAME=\"Test \\\"Linux\\\" 1.0\"\n";
    AssertEqual("Test \"Linux\" 1.0", LinuxSystemCaptureService.ParseOsRelease(input));
}

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Erwartet '{expected}', erhalten '{actual}'.");
    }
}
