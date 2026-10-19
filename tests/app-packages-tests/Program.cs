extern alias SilkSkiaGl;
extern alias SilkSkiaCpu;
extern alias SilkManaged;
extern alias WaylandManaged;
extern alias WaylandSkiaCpu;

using System.Reflection;

// An app changes how it is drawn by changing its Bibui.App package and nothing
// in its code. That holds only while every package's BibuiApp (and its options)
// has the same public members: this keeps them so. The window and renderer are
// the one difference, by design: their names are checked, not their types.
var packages = new (string Name, Type App, Type Options)[]
{
    ("SilkSkiaGl", typeof(SilkSkiaGl::Bibui.App.BibuiApp), typeof(SilkSkiaGl::Bibui.App.BibuiAppOptions)),
    ("SilkSkiaCpu", typeof(SilkSkiaCpu::Bibui.App.BibuiApp), typeof(SilkSkiaCpu::Bibui.App.BibuiAppOptions)),
    ("SilkManaged", typeof(SilkManaged::Bibui.App.BibuiApp), typeof(SilkManaged::Bibui.App.BibuiAppOptions)),
    ("WaylandManaged", typeof(WaylandManaged::Bibui.App.BibuiApp), typeof(WaylandManaged::Bibui.App.BibuiAppOptions)),
    ("WaylandSkiaCpu", typeof(WaylandSkiaCpu::Bibui.App.BibuiApp), typeof(WaylandSkiaCpu::Bibui.App.BibuiAppOptions)),
};

static string TypeName(Type t) =>
    t.FullName!.StartsWith("Bibui.App.", StringComparison.Ordinal) ? t.Name   // the package's own types, by name
    : t.IsGenericType ? $"{t.Name}[{string.Join(",", t.GetGenericArguments().Select(TypeName))}]"
    : t.FullName!;

// A type as declared, nullability included: Action? and Action differ.
static string Declared(Type t, NullabilityInfo n) =>
    TypeName(t) + (!t.IsValueType && n.ReadState == NullabilityState.Nullable ? "?" : "");

static string[] Api(Type type)
{
    var nullability = new NullabilityInfoContext();
    var members = new List<string>();
    const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (var m in type.GetMembers(Public))
    {
        switch (m)
        {
            case FieldInfo f:
                members.Add(f.Name is "Window" or "Renderer"
                    ? $"field {(f.IsInitOnly ? "readonly " : "")}{f.Name}"
                    : $"field {(f.IsInitOnly ? "readonly " : "")}{Declared(f.FieldType, nullability.Create(f))} {f.Name}");
                break;
            case PropertyInfo p:
            {
                // init is a modreq on the setter's return: the same name, a different promise.
                var set = p.SetMethod is { IsPublic: true } setter
                    ? setter.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.Name == "IsExternalInit") ? " init" : " set"
                    : "";
                members.Add($"property {Declared(p.PropertyType, nullability.Create(p))} {p.Name} {(p.CanRead ? "get" : "")}{set}");
                break;
            }
            case EventInfo e:
                members.Add($"event {TypeName(e.EventHandlerType!)} {e.Name}");
                break;
            case MethodInfo mi when !mi.IsSpecialName:
                members.Add($"method {(mi.IsStatic ? "static " : "")}{Declared(mi.ReturnType, nullability.Create(mi.ReturnParameter))} {mi.Name}("
                    + string.Join(", ", mi.GetParameters().Select(x => $"{Declared(x.ParameterType, nullability.Create(x))} {x.Name}{(x.HasDefaultValue ? " = default" : "")}")) + ")");
                break;
            case ConstructorInfo c:
                members.Add($"constructor ({string.Join(", ", c.GetParameters().Select(x => Declared(x.ParameterType, nullability.Create(x))))})");
                break;
            case Type nested:
                members.Add($"nested {nested.Name}");
                break;
        }
    }
    members.Sort(StringComparer.Ordinal);
    return [.. members];
}

int failures = 0;
var (firstName, firstApp, firstOptions) = packages[0];
foreach (var (name, app, options) in packages.Skip(1))
{
    foreach (var (what, mine, theirs) in new[] { ("BibuiApp", Api(app), Api(firstApp)), ("BibuiAppOptions", Api(options), Api(firstOptions)) })
    {
        var missing = theirs.Except(mine).ToArray();
        var extra = mine.Except(theirs).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
        {
            Console.WriteLine($"  [PASS] {name}'s {what} has {firstName}'s public members ({mine.Length})");
            continue;
        }
        failures++;
        Console.WriteLine($"  [FAIL] {name}'s {what} differs from {firstName}'s");
        foreach (var m in missing) Console.WriteLine($"         missing: {m}");
        foreach (var m in extra) Console.WriteLine($"         extra:   {m}");
    }
}

// The renderer and window really are the package's: what the name promises.
string[] expected = ["SkiaGlRenderer/SilkWindow", "SkiaCpuRenderer/SilkWindow", "ManagedRenderer/SilkWindow", "ManagedRenderer/WaylandWindow", "SkiaCpuRenderer/WaylandWindow"];
for (int i = 0; i < packages.Length; i++)
{
    var got = $"{packages[i].App.GetField("Renderer")!.FieldType.Name}/{packages[i].App.GetField("Window")!.FieldType.Name}";
    bool ok = got == expected[i];
    if (!ok) failures++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] Bibui.App.{packages[i].Name} is {got}");
}

Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} failed.");
return failures == 0 ? 0 : 1;
