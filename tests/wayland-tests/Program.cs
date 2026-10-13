// bibui-wayland-tests: the Wayland route, headless.
//
//   dotnet run --project tests/wayland-tests -c Release            # everything
//   dotnet run --project tests/wayland-tests -c Release -- scanner # names containing "scanner"
//   dotnet run --project tests/wayland-tests -c Release -- --dump-tables FILE
//       writes the live wl_interface tables for tools/wayland-scanner/check_tables.py
//
// No test opens a window or connects to a compositor.

using Bibui.WaylandTests;

if (args.Length >= 2 && args[0] == "--dump-tables")
{
    File.WriteAllLines(args[1], TableTests.DumpAll());
    Console.WriteLine($"wrote {args[1]}");
    return 0;
}

var t = new Harness(args.Length > 0 ? args[0] : null);
ScannerTests.Register(t);
TableTests.Register(t);
HostTests.Register(t);
ClipboardTests.Register(t);
InputTests.Register(t);
ScaleTests.Register(t);
return t.Finish();

namespace Bibui.WaylandTests
{
    /// <summary>Thrown by a test whose machine lacks what it needs (XML, libwayland, python3).</summary>
    internal sealed class SkipException(string why) : Exception(why);

    internal sealed class Harness(string? filter)
    {
        private int _passed, _failed, _skipped;

        public void Run(string name, Action test)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                test();
                Console.WriteLine($"  [PASS] {name}");
                _passed++;
            }
            catch (SkipException s)
            {
                Console.WriteLine($"  [SKIP] {name}: {s.Message}");
                _skipped++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"  [FAIL] {name}");
                foreach (var line in e.ToString().Split('\n').Take(12)) Console.WriteLine($"         {line}");
                _failed++;
            }
        }

        public int Finish()
        {
            Console.WriteLine($"\n{_passed} passed, {_failed} failed, {_skipped} skipped");
            return _failed == 0 ? 0 : 1;
        }
    }

    internal static class Assert
    {
        public static void Eq<T>(T expected, T actual, string? what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{what ?? "value"}: expected {expected}, got {actual}");
        }

        public static void True(bool value, string what)
        {
            if (!value) throw new Exception(what);
        }

        public static void Contains(string haystack, string needle)
        {
            if (!haystack.Contains(needle, StringComparison.Ordinal))
                throw new Exception($"expected to find:\n    {needle}");
        }

        public static T Throws<T>(Action a) where T : Exception
        {
            try { a(); }
            catch (T e) { return e; }
            throw new Exception($"expected {typeof(T).Name}");
        }
    }
}
