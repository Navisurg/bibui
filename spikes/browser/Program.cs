// Spike: the counter sample (samples/counter) in the browser, on the shared
// browser host. Main names what a canvas can build; bibui-host.js mounts it.
using Bibui.Browser;
using Bibui.Runtime;

int count = 0;

// samples/counter's build, unchanged.
void Counter(BibuiRuntime runtime)
{
    using (runtime.PushParent(runtime.ColumnFit()))
    {
        runtime.Text($"Clicked {count} times");
        if (runtime.Button("add", "Add one").Clicked) count++;
    }
}

// A heavier scene: a grid of 96 labelled buttons.
void Heavy(BibuiRuntime runtime)
{
    using (runtime.PushParent(runtime.ColumnFit()))
    {
        runtime.Text($"Clicked {count} times");
        for (int r = 0; r < 12; r++)
            using (runtime.PushParent(runtime.RowFit()))
                for (int c = 0; c < 8; c++)
                    if (runtime.Button($"b{r}_{c}", $"Button {r * 8 + c}").Clicked) count++;
    }
}

if (OperatingSystem.IsBrowser())
{
    BrowserHost.Ids = () => ["counter", "heavy"];
    BrowserHost.Factory = id => id switch { "counter" => Counter, "heavy" => Heavy, _ => null };
}
Console.WriteLine("browser-counter: ready");
