// bibui-portal-tests: Bibui.Platform.Portal, headless.
//
//   dotnet run --project tests/portal-tests -c Release            # everything
//   dotnet run --project tests/portal-tests -c Release -- uri     # names containing "uri"
//
// No test opens a dialog. The file chooser is driven against a fake bus on a
// socket of its own; the one live probe asks the session bus's portal for its
// FileChooser version and nothing else.

using Bibui.PortalTests;

var t = new Harness(args.Length > 0 ? args[0] : null);
WireTests.Register(t);
UriTests.Register(t);
ChooserTests.Register(t);
return t.Finish();

namespace Bibui.PortalTests
{
    /// <summary>Thrown by a test whose machine lacks what it needs (python3 with GLib, a session bus).</summary>
    internal sealed class SkipException(string why) : Exception(why);

    internal sealed class Harness(string? filter)
    {
        private int _passed, _failed, _skipped;

        public void Run(string name, Func<Task> test) => Run(name, () => test().GetAwaiter().GetResult());

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
                throw new Exception($"expected to find:\n    {needle}\nin:\n    {haystack}");
        }

        public static T Throws<T>(Action a) where T : Exception
        {
            try { a(); }
            catch (T e) { return e; }
            throw new Exception($"expected {typeof(T).Name}");
        }

        public static async Task<T> ThrowsAsync<T>(Func<Task> a) where T : Exception
        {
            try { await a(); }
            catch (T e) { return e; }
            throw new Exception($"expected {typeof(T).Name}");
        }
    }
}
