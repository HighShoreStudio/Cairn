using System.Text;
using System.Text.RegularExpressions;

namespace HighshoreCairn.Services;

/// <summary>Text = markdown / plain text; Rich = rich text (.rtf).</summary>
public enum DocKind { Folder, Text, Rich, Image, Other }

/// <summary>A folder or a file of the documentation tree.</summary>
public class DocNode
{
    /// <summary>Path relative to the docs folder, with '/' separators ("" = the docs folder itself).</summary>
    public string Path { get; set; } = "";
    /// <summary>Name shown in the app (for a linked file: the name of the original, without ".link").</summary>
    public string Name { get; set; } = "";
    public DocKind Kind { get; set; }
    public bool IsFolder => Kind == DocKind.Folder;
    /// <summary>The file is a pointer to a document that lives outside the project.</summary>
    public bool IsLink { get; set; }
    public string? LinkTarget { get; set; }
    /// <summary>A link whose original file no longer exists.</summary>
    public bool IsBroken { get; set; }
    public List<DocNode> Children { get; set; } = new();
    public DateTime Modified { get; set; }
    public long Size { get; set; }

    /// <summary>True for the documents edited in the app: markdown / plain text and rich text.</summary>
    public bool IsDocument => Kind is DocKind.Text or DocKind.Rich;
    /// <summary>Name without the extension of the documents ("Design.md" -> "Design").</summary>
    public string Title => IsDocument ? DocsService.TitleOf(Name) : Name;
    /// <summary>Path used by links: the path with the display name (so without ".link").</summary>
    public string DisplayPath => DocsService.Combine(DocsService.ParentOf(Path), Name);
}

/// <summary>A reference found in the text of a document.</summary>
public class DocLink
{
    /// <summary>The target as written, including an optional "#heading" (and "&lt;&gt;" for markdown links).</summary>
    public string Raw { get; set; } = "";
    /// <summary>The target without "#heading", decoded.</summary>
    public string Target { get; set; } = "";
    public string? Anchor { get; set; }
    public bool IsWiki { get; set; }
    public bool IsImage { get; set; }
    /// <summary>Position of <see cref="Raw"/> in the text.</summary>
    public int Start { get; set; }
    public int Length { get; set; }
}

public class DocSearchHit
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>0-based line of the match, or -1 when the file name matched.</summary>
    public int Line { get; set; } = -1;
    public string Snippet { get; set; } = "";
}

public class DocGraphNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Top-level folder of the document: nodes of the same group share a color.</summary>
    public string Group { get; set; } = "";
    /// <summary>A document that is referenced but does not exist yet.</summary>
    public bool IsGhost { get; set; }
    public int Degree { get; set; }
}

public class DocGraphEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public class DocGraph
{
    public List<DocGraphNode> Nodes { get; set; } = new();
    public List<DocGraphEdge> Edges { get; set; } = new();
}

/// <summary>Content of a "Name.ext.link" file: a pointer to a document outside the project.</summary>
public class DocLinkFile
{
    public string Target { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Lookup of the documents of a project by path and by name (used to resolve links).</summary>
public class DocIndex
{
    private readonly Dictionary<string, DocNode> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DocNode>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public DocIndex(IEnumerable<DocNode> files)
    {
        foreach (var file in files)
        {
            Files.Add(file);
            _byPath[file.DisplayPath] = file;
            if (file.IsDocument) _byPath.TryAdd(DocsService.TitleOf(file.DisplayPath), file);
            Add(file.Name, file);
            if (file.IsDocument) Add(file.Title, file);
        }
    }

    public List<DocNode> Files { get; } = new();

    private void Add(string name, DocNode node)
    {
        if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<DocNode>();
        if (!list.Contains(node)) list.Add(node);
    }

    public DocNode? ByPath(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>Finds the document a link points to. null for web links and for targets that do not exist.</summary>
    public DocNode? Resolve(string fromPath, string target, bool isWiki)
    {
        target = (target ?? "").Trim().Replace('\\', '/');
        if (target.Length == 0 || MdDoc.IsUrl(target) || target.Contains(':')) return null;
        var folder = DocsService.ParentOf(fromPath);

        if (isWiki)
        {
            var clean = target.TrimStart('/');
            // 1. a path from the docs folder, or from the folder of the document
            if (_byPath.TryGetValue(clean, out var exact)) return exact;
            var relative = DocsService.NormalizePath(DocsService.Combine(folder, clean));
            if (relative != null && _byPath.TryGetValue(relative, out exact)) return exact;
            // 2. a name: the closest document wins (same folder first, then the shortest path)
            var name = clean.Contains('/') ? clean[(clean.LastIndexOf('/') + 1)..] : clean;
            if (clean.Contains('/') || !_byName.TryGetValue(name, out var candidates)) return null;
            return candidates
                .OrderBy(c => DocsService.ParentOf(c.Path).Equals(folder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Path.Count(ch => ch == '/'))
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        var path = target.StartsWith('/')
            ? DocsService.NormalizePath(target.TrimStart('/'))
            : DocsService.NormalizePath(DocsService.Combine(folder, target));
        return path != null && _byPath.TryGetValue(path, out var node) ? node : null;
    }
}

/// <summary>
/// The Documentation area of a project. Everything lives in the "docs" folder of the project and the
/// tree shown in the app is exactly that folder: sub-folders are folders, documents are plain files.
///   docs/**/*.md|.txt        documents edited in the app
///   docs/**/Name.ext.link    pointer to an external document (JSON with the original path)
///   docs/.assets/            pictures pasted into the documents
///   docs/.trash/             deleted items (never shown; can be recovered by hand)
/// </summary>
public partial class DocsService
{
    public const string FolderName = "docs";
    public const string AssetsFolder = ".assets";
    public const string TrashFolder = ".trash";
    public const string LinkExtension = ".link";

    public static readonly string[] TextExtensions = { ".md", ".markdown", ".txt" };
    public const string RichExtension = ".rtf";
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    private readonly string _projectFolder;

    public DocsService(string projectFolder) => _projectFolder = projectFolder;

    public string Root => System.IO.Path.Combine(_projectFolder, FolderName);

    public void EnsureRoot() => Directory.CreateDirectory(Root);

    // ------------------------------------------------------------------ path helpers

    public static string Combine(string folder, string name) =>
        string.IsNullOrEmpty(folder) ? name : string.IsNullOrEmpty(name) ? folder : folder + "/" + name;

    public static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..].ToLowerInvariant();
    }

    public static bool IsTextName(string name) => TextExtensions.Contains(ExtensionOf(name));
    public static bool IsRichName(string name) => ExtensionOf(name) == RichExtension;
    public static bool IsImageName(string name) => ImageExtensions.Contains(ExtensionOf(name));

    /// <summary>"Design.md" -> "Design" (only the extensions of text documents are removed).</summary>
    public static string TitleOf(string name)
    {
        var file = name;
        return IsTextName(file) || IsRichName(file) ? file[..file.LastIndexOf('.')] : file;
    }

    /// <summary>Resolves "." and ".." segments. null when the path leaves the docs folder.</summary>
    public static string? NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join("/", parts);
    }

    /// <summary>The relative link from one document to another ("../Design/Combat.md").</summary>
    public static string RelativeLink(string fromPath, string toPath)
    {
        var from = ParentOf(fromPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = toPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < from.Length && common < to.Length - 1 &&
               string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase)) common++;
        var sb = new StringBuilder();
        for (var i = common; i < from.Length; i++) sb.Append("../");
        sb.Append(string.Join("/", to.Skip(common)));
        return sb.ToString();
    }

    /// <summary>Absolute path of an item of the tree. Throws when the path would leave the docs folder.</summary>
    public string FullPath(string path)
    {
        var normalized = NormalizePath(path ?? "") ?? throw new InvalidOperationException("Invalid document path.");
        return normalized.Length == 0 ? Root : System.IO.Path.Combine(Root, normalized.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Returns an error message, or null when the name can be used for a file or a folder.</summary>
    public static string? ValidateName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "The name is required.";
        if (name.Length > 120) return "The name is too long.";
        if (name.StartsWith('.')) return "The name cannot start with a dot.";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "The name cannot end with a dot or a space.";
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl)) return "The name cannot contain  \\ / : * ? \" < > |";
        if (name.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase)) return "The name cannot end with .link";
        return null;
    }

    /// <summary>A name that does not exist yet in the folder ("Notes.md", "Notes 2.md", ...).</summary>
    private string UniqueName(string folder, string name, string suffix = "")
    {
        var dir = FullPath(folder);
        var ext = IsTextName(name) || name.Contains('.') ? name[name.LastIndexOf('.')..] : "";
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        var candidate = name;
        var n = 2;
        while (File.Exists(System.IO.Path.Combine(dir, candidate + suffix)) ||
               Directory.Exists(System.IO.Path.Combine(dir, candidate + suffix)) ||
               File.Exists(System.IO.Path.Combine(dir, candidate)) ||
               File.Exists(System.IO.Path.Combine(dir, candidate + LinkExtension)))
            candidate = $"{stem} {n++}{ext}";
        return candidate;
    }

    // ------------------------------------------------------------------ tree

    /// <summary>The whole documentation tree (folders first, then files, by name).</summary>
    public DocNode Tree()
    {
        var root = new DocNode { Path = "", Name = "Documentation", Kind = DocKind.Folder };
        if (Directory.Exists(Root)) Fill(root, Root, 0);
        return root;
    }

    private void Fill(DocNode parent, string dir, int depth)
    {
        if (depth > 24) return;
        IEnumerable<string> folders, files;
        try
        {
            folders = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch
        {
            return; // unreadable folder: show it empty
        }

        foreach (var folder in folders.OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(folder);
            if (name.StartsWith('.')) continue;
            var node = new DocNode { Path = Combine(parent.Path, name), Name = name, Kind = DocKind.Folder };
            Fill(node, folder, depth + 1);
            parent.Children.Add(node);
        }

        var nodes = new List<DocNode>();
        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var node = new DocNode { Path = Combine(parent.Path, name), Name = name };
            try
            {
                var info = new FileInfo(file);
                node.Modified = info.LastWriteTime;
                node.Size = info.Length;
            }
            catch { /* details are optional */ }

            if (name.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase))
            {
                node.IsLink = true;
                node.Name = name[..^LinkExtension.Length];
                try
                {
                    node.LinkTarget = JsonFile.Load<DocLinkFile>(file)?.Target;
                }
                catch { /* broken pointer file */ }
                node.IsBroken = string.IsNullOrEmpty(node.LinkTarget) || !File.Exists(node.LinkTarget);
                if (!node.IsBroken)
                {
                    try
                    {
                        var info = new FileInfo(node.LinkTarget!);
                        node.Modified = info.LastWriteTime;
                        node.Size = info.Length;
                    }
                    catch { /* details are optional */ }
                }
            }
            node.Kind = IsTextName(node.Name) ? DocKind.Text : IsRichName(node.Name) ? DocKind.Rich
                : IsImageName(node.Name) ? DocKind.Image : DocKind.Other;
            nodes.Add(node);
        }
        parent.Children.AddRange(nodes.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>Every file of the tree (no folders).</summary>
    public List<DocNode> Files()
    {
        var result = new List<DocNode>();
        void Walk(DocNode node)
        {
            foreach (var child in node.Children)
            {
                if (child.IsFolder) Walk(child);
                else result.Add(child);
            }
        }
        Walk(Tree());
        return result;
    }

    public DocIndex Index() => new(Files());

    public DocNode? Find(string path)
    {
        DocNode? Search(DocNode node)
        {
            foreach (var child in node.Children)
            {
                if (string.Equals(child.Path, path, StringComparison.OrdinalIgnoreCase)) return child;
                if (child.IsFolder && Search(child) is { } found) return found;
            }
            return null;
        }
        return Search(Tree());
    }

    // ------------------------------------------------------------------ content

    /// <summary>The file that holds the content: the document itself, or the original of a link.</summary>
    public string ContentPath(string path)
    {
        var full = FullPath(path);
        if (!path.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase)) return full;
        var target = JsonFile.Load<DocLinkFile>(full)?.Target;
        if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("The link file is damaged.");
        return target;
    }

    public string Read(string path) => Read(path, out _);

    /// <summary>Reads a document and tells which encoding its file uses (see <see cref="ReadText"/>).</summary>
    public string Read(string path, out Encoding encoding)
    {
        var file = ContentPath(path);
        if (!File.Exists(file))
            throw new FileNotFoundException("The original file of this link no longer exists:\n" + file);
        return ReadText(file, out encoding);
    }

    /// <summary>
    /// Writes a document, in the encoding its file already had (UTF-8 for new files).
    /// Returns the encoding actually used.
    /// </summary>
    public Encoding Write(string path, string text, Encoding? encoding = null) =>
        WriteText(ContentPath(path), text, encoding ?? Utf8);

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var page = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(page > 0 ? page : 1252);
        }
        catch
        {
            return Encoding.Latin1;
        }
    });

    /// <summary>
    /// Reads a text file without guessing wrong: a byte-order mark decides; otherwise the file is UTF-8 when
    /// its bytes are valid UTF-8, and an old Windows "ANSI" file when they are not (documents imported or
    /// linked from other programs). The encoding found is returned so the file is written back the same way.
    /// </summary>
    public static string ReadText(string file, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            return encoding.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            return encoding.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            return encoding.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            encoding = Utf8;
            return text;
        }
        catch (DecoderFallbackException)
        {
            encoding = Ansi.Value;
            return encoding.GetString(bytes);
        }
    }

    /// <summary>Writes a text file through a temporary file, with the given encoding (and its byte-order mark).</summary>
    public static Encoding WriteText(string file, string text, Encoding encoding)
    {
        // A character the old encoding cannot store would become "?": the file is upgraded to UTF-8 instead.
        if (encoding.IsSingleByte && encoding.GetString(encoding.GetBytes(text)) != text) encoding = Utf8;

        var dir = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = file + ".tmp";
        using (var stream = File.Create(tmp))
        {
            var preamble = encoding.GetPreamble();
            stream.Write(preamble, 0, preamble.Length);
            var bytes = encoding.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
        File.Move(tmp, file, overwrite: true);
        return encoding;
    }

    public DateTime LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(ContentPath(path)); }
        catch { return DateTime.MinValue; }
    }

    // ------------------------------------------------------------------ create / rename / move / delete

    /// <summary>Creates a text document (".md" is added when the name has no text extension). Returns its path.</summary>
    public string CreateDocument(string folder, string name, string content = "")
    {
        name = (name ?? "").Trim();
        var error = ValidateName(name);
        if (error != null) throw new InvalidOperationException(error);
        if (!IsTextName(name)) name += ".md";
        EnsureRoot();
        Directory.CreateDirectory(FullPath(folder));
        name = UniqueName(folder, name);
        var path = Combine(folder, name);
        JsonFile.WriteAllTextAtomic(FullPath(path), content);
        return path;
    }

    /// <summary>An empty rich text document, as WordPad or Word would create it.</summary>
    public const string EmptyRtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil Segoe UI;}}\pard\f0\fs24\par}";

    /// <summary>Creates a rich text document (".rtf" is added when missing). Returns its path.</summary>
    public string CreateRichDocument(string folder, string name, string? rtf = null)
    {
        name = (name ?? "").Trim();
        var error = ValidateName(name);
        if (error != null) throw new InvalidOperationException(error);
        if (!IsRichName(name)) name += RichExtension;
        EnsureRoot();
        Directory.CreateDirectory(FullPath(folder));
        name = UniqueName(folder, name);
        var path = Combine(folder, name);
        JsonFile.WriteAllTextAtomic(FullPath(path), rtf ?? EmptyRtf);
        return path;
    }

    /// <summary>
    /// Replaces a document with its conversion to the other format (".md" &lt;-&gt; ".rtf"): the file gets the
    /// new extension and the new content, the links of the other documents follow it, and a copy of the
    /// original goes to docs/.trash. Returns the new path.
    /// </summary>
    public string Convert(string path, string newContent, bool toRich)
    {
        var full = FullPath(path);
        var name = FileNameOf(path);
        if (!File.Exists(full) || name.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only documents stored in the project can be converted.");

        var extension = toRich ? RichExtension : ".md";
        var folder = ParentOf(path);
        var newName = UniqueName(folder, TitleOf(name) + extension);
        var newPath = Combine(folder, newName);

        // Nothing is lost: the original stays in the trash.
        var trash = System.IO.Path.Combine(Root, TrashFolder);
        Directory.CreateDirectory(trash);
        var backup = System.IO.Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}");
        var n = 2;
        while (File.Exists(backup)) backup = System.IO.Path.Combine(trash, $"{DateTime.Now:yyyyMMdd-HHmmss}-{n++}-{name}");
        File.Copy(full, backup);

        Relocate(path, newPath);
        try
        {
            JsonFile.WriteAllTextAtomic(FullPath(newPath), newContent);
        }
        catch
        {
            // The new content could not be written: put the document back as it was.
            try { Relocate(newPath, path); } catch { /* the original is in the trash in any case */ }
            throw;
        }
        return newPath;
    }

    /// <summary>True when docs/.trash of a project contains something.</summary>
    public static bool HasTrash(string projectFolder)
    {
        try
        {
            var trash = TrashPath(projectFolder);
            return Directory.Exists(trash) && Directory.EnumerateFileSystemEntries(trash).Any();
        }
        catch { return false; }
    }

    public static string TrashPath(string projectFolder) =>
        System.IO.Path.Combine(projectFolder, FolderName, TrashFolder);

    public string CreateFolder(string folder, string name)
    {
        name = (name ?? "").Trim();
        var error = ValidateName(name);
        if (error != null) throw new InvalidOperationException(error);
        EnsureRoot();
        name = UniqueName(folder, name);
        var path = Combine(folder, name);
        Directory.CreateDirectory(FullPath(path));
        return path;
    }

    /// <summary>
    /// Renames a document or a folder and updates the links of the other documents that point to it.
    /// A text document keeps its extension when the new name has none. Returns the new path.
    /// </summary>
    public string Rename(string path, string newName)
    {
        newName = (newName ?? "").Trim();
        var error = ValidateName(newName);
        if (error != null) throw new InvalidOperationException(error);

        var full = FullPath(path);
        var isFolder = Directory.Exists(full);
        var fileName = FileNameOf(path);
        var isLink = !isFolder && fileName.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase);
        var display = isLink ? fileName[..^LinkExtension.Length] : fileName;

        if (!isFolder)
        {
            // Keep the kind of the file: "Notes" renames "Old.md" to "Notes.md".
            var oldExt = ExtensionOf(display);
            if (oldExt.Length > 0 && ExtensionOf(newName) != oldExt && !(IsTextName(display) && IsTextName(newName)))
                newName += display[display.LastIndexOf('.')..];
        }

        var target = Combine(ParentOf(path), newName + (isLink ? LinkExtension : ""));
        if (string.Equals(target, path, StringComparison.Ordinal)) return path;
        return Relocate(path, target);
    }

    /// <summary>Moves a document or a folder into another folder, updating the links. Returns the new path.</summary>
    public string Move(string path, string targetFolder)
    {
        targetFolder = NormalizePath(targetFolder ?? "") ?? "";
        if (string.Equals(ParentOf(path), targetFolder, StringComparison.OrdinalIgnoreCase)) return path;
        if (targetFolder.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            targetFolder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A folder cannot be moved into itself.");
        Directory.CreateDirectory(FullPath(targetFolder));
        return Relocate(path, Combine(targetFolder, FileNameOf(path)));
    }

    private string Relocate(string oldPath, string newPath)
    {
        var oldFull = FullPath(oldPath);
        var newFull = FullPath(newPath);
        var isFolder = Directory.Exists(oldFull);
        if (!isFolder && !File.Exists(oldFull)) throw new InvalidOperationException("The item no longer exists.");

        var caseOnly = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(newFull) || Directory.Exists(newFull)))
            throw new InvalidOperationException($"'{FileNameOf(newPath)}' already exists in that folder.");

        string Map(string p)
        {
            if (p.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) return newPath;
            if (isFolder && p.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) return newPath + p[oldPath.Length..];
            return p;
        }

        // 1. Before moving: remember where every link of every document points to.
        var index = Index();
        var pending = new List<(DocNode Doc, string Text, Encoding Encoding, List<(DocLink Link, DocNode Target)> Links)>();
        foreach (var doc in index.Files.Where(f => f.Kind == DocKind.Text && !f.IsLink))
        {
            string text;
            Encoding encoding;
            try { text = ReadText(FullPath(doc.Path), out encoding); }
            catch { continue; }
            var links = new List<(DocLink, DocNode)>();
            var docMoves = Map(doc.Path) != doc.Path;
            foreach (var link in ExtractLinks(text))
            {
                var target = index.Resolve(doc.Path, link.Target, link.IsWiki);
                if (target is null) continue;
                var targetMoves = Map(target.Path) != target.Path;
                // A relative markdown link changes when either end moves to another folder.
                if (targetMoves || (docMoves && !link.IsWiki)) links.Add((link, target));
            }
            if (links.Count > 0) pending.Add((doc, text, encoding, links));
        }

        // 2. Move.
        if (isFolder) Directory.Move(oldFull, newFull);
        else File.Move(oldFull, newFull);

        // 3. Rewrite the links.
        foreach (var (doc, text, encoding, links) in pending)
        {
            var docPath = Map(doc.Path);
            var sb = new StringBuilder(text);
            foreach (var (link, target) in links.OrderByDescending(l => l.Link.Start))
            {
                var targetPath = Map(target.Path);
                var display = targetPath.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase)
                    ? targetPath[..^LinkExtension.Length] : targetPath;
                string raw;
                if (link.IsWiki)
                {
                    // Keep the style of the link: a bare name stays a bare name, a path stays a path.
                    var hadExtension = IsTextName(link.Target);
                    var form = link.Target.Contains('/') ? display : FileNameOf(display);
                    if (!hadExtension) form = Combine(ParentOf(form), TitleOf(FileNameOf(form)));
                    raw = form + (link.Anchor is null ? "" : "#" + link.Anchor);
                }
                else
                {
                    raw = MdDoc.LinkTarget(RelativeLink(docPath, display) + (link.Anchor is null ? "" : "#" + link.Anchor));
                }
                if (raw == link.Raw) continue;
                sb.Remove(link.Start, link.Length).Insert(link.Start, raw);
            }
            var updated = sb.ToString();
            if (updated == text) continue;
            try { WriteText(FullPath(docPath), updated, encoding); }
            catch { /* the move itself succeeded: a link that could not be updated is not fatal */ }
        }
        return newPath;
    }

    /// <summary>
    /// Removes a document or a folder from the tree. Nothing is destroyed: the item is moved to docs/.trash
    /// (for a link only the pointer is removed, never the original file).
    /// </summary>
    public void Delete(string path)
    {
        var full = FullPath(path);
        var isFolder = Directory.Exists(full);
        if (!isFolder && !File.Exists(full)) return;
        if (path.Length == 0) throw new InvalidOperationException("The documentation folder cannot be deleted.");

        var trash = System.IO.Path.Combine(Root, TrashFolder);
        Directory.CreateDirectory(trash);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{FileNameOf(path)}";
        var target = System.IO.Path.Combine(trash, name);
        var n = 2;
        while (File.Exists(target) || Directory.Exists(target)) target = System.IO.Path.Combine(trash, $"{name}-{n++}");
        if (isFolder) Directory.Move(full, target);
        else File.Move(full, target);
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Adds an external file (or folder) to the documentation: either a copy inside the project
    /// or a link to the original. Returns the path of the new item.
    /// </summary>
    public string Import(string source, string folder, bool link)
    {
        EnsureRoot();
        Directory.CreateDirectory(FullPath(folder));
        source = System.IO.Path.GetFullPath(source);

        var rootFull = System.IO.Path.GetFullPath(Root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var sourceAsFolder = source.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

        if (Directory.Exists(source))
        {
            // A folder that contains the documentation (or is part of it) would be copied into itself forever.
            if (rootFull.StartsWith(sourceAsFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This folder contains the documentation of the project: it cannot be imported into it.");
            if (sourceAsFolder.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return sourceAsFolder[rootFull.Length..].TrimEnd(System.IO.Path.DirectorySeparatorChar).Replace(System.IO.Path.DirectorySeparatorChar, '/');

            var name = UniqueName(folder, System.IO.Path.GetFileName(source.TrimEnd(System.IO.Path.DirectorySeparatorChar)));
            var target = Combine(folder, name);
            Directory.CreateDirectory(FullPath(target));
            foreach (var dir in Directory.GetDirectories(source))
                if (!System.IO.Path.GetFileName(dir).StartsWith('.')) Import(dir, target, link);
            foreach (var file in Directory.GetFiles(source))
                if (!System.IO.Path.GetFileName(file).StartsWith('.')) Import(file, target, link);
            return target;
        }

        if (!File.Exists(source)) throw new FileNotFoundException("The file does not exist:\n" + source);

        // A file that is already inside the documentation is simply shown.
        if (source.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return source[rootFull.Length..].Replace(System.IO.Path.DirectorySeparatorChar, '/');

        var fileName = System.IO.Path.GetFileName(source);
        if (fileName.EndsWith(LinkExtension, StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^LinkExtension.Length];
        if (link)
        {
            var name = UniqueName(folder, fileName, LinkExtension);
            var path = Combine(folder, name + LinkExtension);
            JsonFile.Save(FullPath(path), new DocLinkFile { Target = source });
            return path;
        }
        else
        {
            var name = UniqueName(folder, fileName);
            var path = Combine(folder, name);
            File.Copy(source, FullPath(path), overwrite: false);
            return path;
        }
    }

    /// <summary>Stores a picture pasted into a document (docs/.assets). Returns its path in the docs folder.</summary>
    public string SaveAsset(byte[] bytes, string extension = ".png")
    {
        var dir = System.IO.Path.Combine(Root, AssetsFolder);
        Directory.CreateDirectory(dir);
        var name = $"image-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}{extension.ToLowerInvariant()}";
        File.WriteAllBytes(System.IO.Path.Combine(dir, name), bytes);
        return AssetsFolder + "/" + name;
    }

    /// <summary>Copies an external picture into docs/.assets. Returns its path in the docs folder.</summary>
    public string ImportAsset(string sourceFile) =>
        SaveAsset(File.ReadAllBytes(sourceFile), System.IO.Path.GetExtension(sourceFile));

    /// <summary>
    /// Absolute path of a picture referenced by a document (relative to the document, or to the docs folder).
    /// null when the file does not exist. Web addresses are returned unchanged.
    /// </summary>
    public string? ResolveImage(string documentPath, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (MdDoc.IsUrl(source)) return source;
        try
        {
            if (System.IO.Path.IsPathFullyQualified(source))
            {
                if (File.Exists(source)) return source;
                if (!source.StartsWith('/')) return null; // "/img/a.png" may also mean "from the docs folder"
            }
            foreach (var candidate in new[] { Combine(ParentOf(documentPath), source), source.TrimStart('/') })
            {
                var normalized = NormalizePath(candidate);
                if (normalized is null) continue;
                var full = FullPath(normalized);
                if (File.Exists(full)) return full;
            }
            // An embed by name (![[picture.png]]): look for the file anywhere in the documentation.
            var name = FileNameOf(source.Replace('\\', '/'));
            var found = Files().FirstOrDefault(f => !f.IsLink && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return found != null ? FullPath(found.Path) : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ links

    // The label separator can be written "\|" (inside tables): it is not part of the target.
    [GeneratedRegex(@"(!?)\[\[([^\]\|\n]+?)(?:\\?\|[^\]\n]*)?\]\]")] private static partial Regex WikiRx();
    [GeneratedRegex(@"(!?)\[(?:[^\]\\\n]|\\.)*\]\(\s*(<[^>\n]*>|[^)\s]+)(?:\s+(?:""[^""\n]*""|'[^'\n]*'))?\s*\)")] private static partial Regex MdLinkRx();
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")] private static partial Regex FenceStartRx();
    [GeneratedRegex(@"(`+)(?:(?!\1).)+?\1")] private static partial Regex InlineCodeRx();

    /// <summary>Finds the links of a markdown text (code blocks and code spans are ignored).</summary>
    public static List<DocLink> ExtractLinks(string? text)
    {
        var links = new List<DocLink>();
        if (string.IsNullOrEmpty(text)) return links;

        // Blank out the code, keeping every character position.
        var masked = new StringBuilder(text);
        var pos = 0;
        string? fence = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var start = FenceStartRx().Match(line);
            if (fence != null)
            {
                var trimmed = line.Trim();
                var closes = trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]);
                for (var i = 0; i < line.Length; i++) masked[pos + i] = ' ';
                if (closes) fence = null;
            }
            else if (start.Success)
            {
                fence = start.Groups[1].Value;
                for (var i = 0; i < line.Length; i++) masked[pos + i] = ' ';
            }
            else
            {
                foreach (Match code in InlineCodeRx().Matches(line))
                    for (var i = 0; i < code.Length; i++) masked[pos + code.Index + i] = ' ';
            }
            pos += rawLine.Length + 1;
        }
        var clean = masked.ToString();

        foreach (Match m in WikiRx().Matches(clean))
        {
            var group = m.Groups[2];
            var raw = group.Value;
            var trimmed = raw.Trim();
            var hash = trimmed.IndexOf('#');
            links.Add(new DocLink
            {
                Raw = raw,
                Target = hash >= 0 ? trimmed[..hash] : trimmed,
                Anchor = hash >= 0 ? trimmed[(hash + 1)..] : null,
                IsWiki = true,
                IsImage = m.Groups[1].Length > 0,
                Start = group.Index,
                Length = group.Length
            });
        }

        foreach (Match m in MdLinkRx().Matches(clean))
        {
            var group = m.Groups[2];
            var raw = group.Value;
            var target = raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>' ? raw[1..^1] : raw.TrimStart('<');
            if (MdDoc.IsUrl(target) || target.StartsWith('#')) continue;
            try { target = Uri.UnescapeDataString(target); }
            catch { /* keep as written */ }
            var hash = target.IndexOf('#');
            links.Add(new DocLink
            {
                Raw = raw,
                Target = hash >= 0 ? target[..hash] : target,
                Anchor = hash >= 0 ? target[(hash + 1)..] : null,
                IsWiki = false,
                IsImage = m.Groups[1].Length > 0,
                Start = group.Index,
                Length = group.Length
            });
        }
        return links.Where(l => l.Target.Length > 0).OrderBy(l => l.Start).ToList();
    }

    /// <summary>The documents (paths) that contain a link to the given one.</summary>
    public List<DocNode> Backlinks(string path)
    {
        var index = Index();
        var result = new List<DocNode>();
        foreach (var doc in index.Files.Where(f => f.Kind == DocKind.Text && !f.IsBroken))
        {
            if (doc.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = Read(doc.Path); }
            catch { continue; }
            if (ExtractLinks(text).Any(l => index.Resolve(doc.Path, l.Target, l.IsWiki)?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true))
                result.Add(doc);
        }
        return result;
    }

    /// <summary>
    /// The graph of the documentation: one node per text document, one edge per reference between two
    /// documents. Links to documents that do not exist yet become "ghost" nodes.
    /// </summary>
    public DocGraph BuildGraph()
    {
        var index = Index();
        var graph = new DocGraph();
        var nodes = new Dictionary<string, DocGraphNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var docs = index.Files.Where(f => f.IsDocument).ToList();
        foreach (var doc in docs)
        {
            var slash = doc.Path.IndexOf('/');
            nodes[doc.Path] = new DocGraphNode { Id = doc.Path, Label = doc.Title, Group = slash < 0 ? "" : doc.Path[..slash] };
        }

        // Only markdown documents contain links; rich text documents can be linked to.
        foreach (var doc in docs.Where(d => !d.IsBroken && d.Kind == DocKind.Text))
        {
            string text;
            try { text = Read(doc.Path); }
            catch { continue; }
            foreach (var link in ExtractLinks(text))
            {
                if (link.IsImage) continue;
                var target = index.Resolve(doc.Path, link.Target, link.IsWiki);
                string id;
                if (target != null)
                {
                    if (!target.IsDocument) continue;
                    id = target.Path;
                }
                else
                {
                    if (!link.IsWiki) continue;
                    id = "?" + link.Target.Trim().ToLowerInvariant();
                    if (!nodes.ContainsKey(id))
                        nodes[id] = new DocGraphNode { Id = id, Label = FileNameOf(link.Target.Trim()), Group = "", IsGhost = true };
                }
                if (id.Equals(doc.Path, StringComparison.OrdinalIgnoreCase)) continue;
                // One edge per pair of documents, whatever the direction.
                var key = string.Compare(doc.Path, id, StringComparison.OrdinalIgnoreCase) < 0 ? doc.Path + "\n" + id : id + "\n" + doc.Path;
                if (!edges.Add(key)) continue;
                graph.Edges.Add(new DocGraphEdge { From = doc.Path, To = id });
                nodes[doc.Path].Degree++;
                nodes[id].Degree++;
            }
        }

        graph.Nodes = nodes.Values.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return graph;
    }

    // ------------------------------------------------------------------ search

    /// <summary>Searches the names and the text of the documents (every word of the query must match).</summary>
    public List<DocSearchHit> Search(string? query, int max = 200)
    {
        var hits = new List<DocSearchHit>();
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return hits;

        bool Matches(string s) => words.All(w => s.Contains(w, StringComparison.CurrentCultureIgnoreCase));

        foreach (var file in Files())
        {
            if (hits.Count >= max) break;
            if (Matches(file.Name)) hits.Add(new DocSearchHit { Path = file.Path, Name = file.Name, Snippet = ParentOf(file.Path) });
            if (!file.IsDocument || file.IsBroken) continue;

            string text;
            try { text = Read(file.Path); }
            catch { continue; }
            var rich = file.Kind == DocKind.Rich;
            if (rich) text = RtfText.ToPlain(text);
            var lines = text.Split('\n');
            var perFile = 0;
            for (var i = 0; i < lines.Length && perFile < 5 && hits.Count < max; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || !Matches(line)) continue;
                var at = Math.Max(0, line.IndexOf(words[0], StringComparison.CurrentCultureIgnoreCase));
                var from = Math.Max(0, at - 40);
                var snippet = (from > 0 ? "…" : "") + line[from..Math.Min(line.Length, from + 140)] + (from + 140 < line.Length ? "…" : "");
                // Lines of a rich text document are not lines of its file: the hit only opens the document.
                hits.Add(new DocSearchHit { Path = file.Path, Name = file.Name, Line = rich ? -1 : i, Snippet = snippet });
                perFile++;
            }
        }
        return hits;
    }
}

/// <summary>Starting content offered by "New document".</summary>
public class DocTemplate
{
    public DocTemplate(string name, string content)
    {
        Name = name;
        Content = content;
    }

    public string Name { get; }
    /// <summary>Markdown with the placeholders {{title}} and {{date}}.</summary>
    public string Content { get; }

    public string Render(string title) =>
        Content.Replace("{{title}}", title).Replace("{{date}}", DateTime.Now.ToString("yyyy-MM-dd"));

    public override string ToString() => Name;

    public static IReadOnlyList<DocTemplate> All { get; } = new[]
    {
        new DocTemplate("Blank document", "# {{title}}\n\n"),
        new DocTemplate("Notes", "# {{title}}\n\n_{{date}}_\n\n- \n"),
        new DocTemplate("Meeting notes",
            "# {{title}}\n\n**Date:** {{date}}  \n**Participants:** \n\n## Agenda\n\n1. \n\n## Notes\n\n- \n\n## Decisions\n\n- \n\n## Action items\n\n- [ ] \n"),
        new DocTemplate("Game design document",
            "# {{title}}\n\n## Overview\n\nOne paragraph that sells the game.\n\n## Pillars\n\n1. \n2. \n3. \n\n## Core loop\n\n\n## Mechanics\n\n| Mechanic | Description | Status |\n| --- | --- | --- |\n|  |  |  |\n\n## Characters & world\n\n\n## Art & audio direction\n\n\n## Scope & milestones\n\n- [ ] Prototype\n- [ ] Vertical slice\n- [ ] Alpha\n- [ ] Beta\n- [ ] Release\n"),
        new DocTemplate("Technical specification",
            "# {{title}}\n\n| | |\n| --- | --- |\n| **Status** | Draft |\n| **Updated** | {{date}} |\n\n## Goal\n\n\n## Non-goals\n\n\n## Design\n\n```csharp\n// key types and methods\n```\n\n## Alternatives considered\n\n\n## Open questions\n\n- [ ] \n"),
        new DocTemplate("Decision record",
            "# {{title}}\n\n**Date:** {{date}}  \n**Status:** Proposed\n\n## Context\n\n\n## Decision\n\n\n## Consequences\n\n"),
        new DocTemplate("Bug report",
            "# {{title}}\n\n**Found:** {{date}}  \n**Severity:** \n\n## Steps to reproduce\n\n1. \n\n## Expected\n\n\n## Actual\n\n\n## Notes\n\n"),
        new DocTemplate("Changelog",
            "# {{title}}\n\n## Unreleased\n\n### Added\n\n- \n\n### Changed\n\n- \n\n### Fixed\n\n- \n"),
        new DocTemplate("To-do list", "# {{title}}\n\n- [ ] \n- [ ] \n- [ ] \n"),
        new DocTemplate("README",
            "# {{title}}\n\nShort description of the project.\n\n## Getting started\n\n\n## Structure\n\n- [[Design]]\n- [[Roadmap]]\n\n## Links\n\n")
    };
}
