// bibui-browser-tests: the browser host's logic, without a browser.
//
//   dotnet run --project tests/browser-tests -c Release            # everything
//   dotnet run --project tests/browser-tests -c Release -- keys    # names containing "keys"

using Bibui.BrowserTests;

var t = new Harness(args.Length > 0 ? args[0] : null);
InputTests.Register(t);
PixelTests.Register(t);
SizeTests.Register(t);
return t.Finish();

namespace Bibui.BrowserTests
{
    internal sealed class Harness(string? filter)
    {
        private int _passed, _failed;

        public void Run(string name, Action test)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                test();
                Console.WriteLine($"  [PASS] {name}");
                _passed++;
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
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
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

        public static void Near(float expected, float actual, float within, string what)
        {
            if (MathF.Abs(expected - actual) > within) throw new Exception($"{what}: expected {expected} ± {within}, got {actual}");
        }
    }
}
