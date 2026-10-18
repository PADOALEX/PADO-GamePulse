using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace GamePulse.Desktop.Services;

/// <summary>Observable resource lookups keep existing controls, focus and loaded profile state intact.</summary>
public sealed class LocalizedResources : INotifyPropertyChanged
{
    private static readonly ConcurrentDictionary<string, LocalizedResources> Sources = new(StringComparer.Ordinal);
    private readonly ResourceManager _manager;
    private readonly ConcurrentDictionary<string, LocalizedText> _texts = new(StringComparer.Ordinal);

    private LocalizedResources(string module)
    {
        _manager = new ResourceManager("GamePulse.Desktop.Resources." + module, typeof(UiLanguage).Assembly);
        UiLanguage.Changed += (_, _) =>
        {
            foreach (var text in _texts.Values) text.Refresh();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        };
    }

    public static LocalizedResources For(string module) => Sources.GetOrAdd(module, name => new(name));
    public string this[string key] => _manager.GetString(key, UiLanguage.Culture) ?? key;
    public LocalizedText Text(string key) => _texts.GetOrAdd(key, value => new(this, value));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A named observable property avoids platform-dependent string-indexer invalidation.</summary>
public sealed class LocalizedText(LocalizedResources resources, string key) : INotifyPropertyChanged
{
    public string Value => resources[key];
    public override string ToString() => Value;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>One-way resource binding, including text in templates, accessibility names and tooltips.</summary>
public sealed class TranslateExtension : MarkupExtension
{
    public string Module { get; set; } = "Strings";
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(LocalizedText.Value))
    {
        Source = LocalizedResources.For(Module).Text(Key), Mode = BindingMode.OneWay
    };
}

/// <summary>
/// Re-renders application-owned status snapshots. Never use this for names, IDs, input or Steam text.
/// Captured date/number arguments are retained, because changing UI language does not change OS formatting.
/// </summary>
public static class LocalizedStatus
{
    private sealed record Entry(string Key, string Text, Regex? Pattern);
    private static readonly Regex Token = new(@"(?<!\{)\{(?<index>\d+)(?:,[^{}:]+)?(?::[^{}]+)?\}(?!\})", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, Entry[]> Entries = new(StringComparer.Ordinal);

    public static string Read(string value, string module)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var target = LocalizedResources.For(module);
        var entries = Entries.GetOrAdd(module, LoadEntries);
        // Summary timestamps predate composite resources; translate their owned prefix/suffix only.
        // No date or number is reparsed, so the user's OS formatting is retained exactly.
        var cachedSuffix = entries.FirstOrDefault(entry => entry.Key == "Cached" &&
            value.EndsWith(" · " + entry.Text, StringComparison.Ordinal));
        if (cachedSuffix is not null)
            return Read(value[..^(cachedSuffix.Text.Length + 3)], module) + " · " + target["Cached"];
        if (module == "Strings")
        {
            var updatedPrefix = entries.FirstOrDefault(entry => entry.Key == "Updated" &&
                value.StartsWith(entry.Text + " ", StringComparison.Ordinal));
            if (updatedPrefix is not null) return target["Updated"] + value[updatedPrefix.Text.Length..];
        }
        // Exact labels take precedence over templates, so a broad placeholder cannot swallow another status.
        foreach (var entry in entries)
            if (entry.Pattern is null && value == entry.Text) return target[entry.Key];
        foreach (var entry in entries)
        {
            if (entry.Pattern is null) continue;
            var match = entry.Pattern.Match(value);
            if (match.Success)
                return Token.Replace(target[entry.Key], token => match.Groups["arg" + token.Groups["index"].Value].Value);
        }
        return value;
    }

    private static Entry[] LoadEntries(string module)
    {
        var manager = new ResourceManager("GamePulse.Desktop.Resources." + module, typeof(UiLanguage).Assembly);
        var entries = new List<Entry>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("uk") })
        {
            if (manager.GetResourceSet(culture, true, false) is not { } set) continue;
            foreach (DictionaryEntry item in set)
            {
                if (item.Key is not string key || item.Value is not string text) continue;
                var tokens = Token.Matches(text);
                Regex? pattern = null;
                if (tokens.Count > 0)
                {
                    var builder = new StringBuilder("\\A");
                    var offset = 0;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Match token in tokens)
                    {
                        builder.Append(Regex.Escape(text[offset..token.Index]));
                        var group = "arg" + token.Groups["index"].Value;
                        builder.Append(seen.Add(group) ? "(?<" + group + ">.*?)" : "\\k<" + group + ">");
                        offset = token.Index + token.Length;
                    }
                    builder.Append(Regex.Escape(text[offset..])).Append("\\z");
                    pattern = new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline,
                        TimeSpan.FromMilliseconds(100));
                }
                entries.Add(new(key, text, pattern));
            }
        }
        return entries.ToArray();
    }
}
