using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>Original client SVG geometry, without executable or externally loaded content.</summary>
public sealed class WaypointIconStore
{
    public const int MaxBytes = 60 * 1024;
    private readonly string directory;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, byte[]> icons = new(StringComparer.OrdinalIgnoreCase);
    public WaypointIconStore(string directory)
    {
        this.directory = directory;
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.svg").Take(256))
        {
            try { if (new FileInfo(file).Length <= MaxBytes) Put(Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(file), false); }
            catch (Exception ex) when (ex is IOException or ArgumentException or XmlException) { }
        }
    }
    public bool Contains(string name) => icons.ContainsKey(name);
    public string[] Names => icons.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public byte[]? Get(string name) => icons.TryGetValue(name, out var bytes) ? bytes.ToArray() : null;
    public void Put(string name, byte[] bytes, bool persist = true)
    {
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,80}$") || bytes.Length is 0 or > MaxBytes) throw new ArgumentException("Invalid waypoint icon");
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
        var doc = XDocument.Load(reader);
        XNamespace svg = "http://www.w3.org/2000/svg";
        if (doc.Root?.Name != svg + "svg") throw new ArgumentException("Expected SVG");
        string[] elements = ["svg", "g", "path", "rect", "circle", "ellipse", "line", "polygon", "polyline", "defs", "linearGradient", "radialGradient", "stop", "clipPath", "mask", "use", "title", "desc"];
        // Strip editor metadata, scripts, animations, images, and foreign content.
        foreach (var node in doc.Root.Descendants().ToArray())
            if (node.Name.Namespace != svg || !elements.Contains(node.Name.LocalName)) node.Remove();
        foreach (var attr in doc.Root.DescendantsAndSelf().Attributes().ToArray())
        {
            if (attr.IsNamespaceDeclaration) continue;
            var key = attr.Name.LocalName;
            if (key.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || key.Equals("base", StringComparison.OrdinalIgnoreCase)
                || key.Equals("href", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(attr.Value, "^#[a-zA-Z0-9_-]+$")
                || Regex.IsMatch(attr.Value, @"url\s*\((?!\s*#[a-zA-Z0-9_-]+\s*\))|@import|expression|\\", RegexOptions.IgnoreCase)) attr.Remove();
        }
        var clean = Encoding.UTF8.GetBytes(doc.Root.ToString(SaveOptions.DisableFormatting));
        if (clean.Length > MaxBytes) throw new ArgumentException("Waypoint icon too large");
        lock (gate)
        {
            if (!icons.ContainsKey(name) && icons.Count >= 256) throw new ArgumentException("Too many waypoint icons");
            if (icons.TryGetValue(name, out var existing) && existing.AsSpan().SequenceEqual(clean)) return;
            if (persist) AtomicFile.Replace(Path.Combine(directory, name + ".svg"), temp => File.WriteAllBytes(temp, clean));
            icons[name] = clean;
        }
    }
}
