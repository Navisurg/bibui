// Bibui.Automation's tests: TestPort, without a socket or a window.
// Run with: dotnet run --project tests/automation-tests

int passed = 0, failed = 0;
string? filter = args.Length > 0 ? args[0] : null;

void Run(string name, Action test)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try
    {
        test();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}");
        Console.WriteLine($"         {ex.Message}");
        failed++;
    }
}

Console.WriteLine("Test port: requests");
TestPortTests.Register(Run);

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine($"Results: {passed} passed, {failed} failed");
if (failed > 0) Environment.Exit(1);
