using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PrintArchive.Core;

/// <summary>
/// Best-effort original-document correlation. Safe against Windows junction /
/// access-denied folders (e.g. Documents\My Music).
/// </summary>
public static class DocumentCapture
{
    private static readonly string[] OfficeExt = [".docx", ".doc", ".docm", ".dotx", ".rtf"];
    private static readonly string[] PhotoshopExt = [".psd", ".psb", ".psdt"];
    private static readonly string[] ImageExt = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif", ".webp"];
    private static readonly string[] PdfExt = [".pdf"];
    private static readonly string[] AllHintExt = [.. OfficeExt, .. PhotoshopExt, .. ImageExt, .. PdfExt, ".xlsx", ".pptx", ".txt"];

    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        // Skip junctions/reparse points like "My Music", "My Pictures".
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        MaxRecursionDepth = 6
    };

    public static CaptureAttempt TryCapture(string documentName, string? username)
    {
        try
        {
            return TryCaptureCore(documentName, username);
        }
        catch (Exception ex)
        {
            // Never let capture failures break print archiving.
            return CaptureAttempt.MetadataOnly($"Capture error: {ex.GetType().Name}");
        }
    }

    private static CaptureAttempt TryCaptureCore(string documentName, string? username)
    {
        if (string.IsNullOrWhiteSpace(documentName))
            return CaptureAttempt.MetadataOnly("Empty document name");

        if (Path.IsPathRooted(documentName) && File.Exists(documentName))
            return CaptureAttempt.Original(documentName);

        string cleaned = CleanDocumentTitle(documentName);
        if (Path.IsPathRooted(cleaned) && File.Exists(cleaned))
            return CaptureAttempt.Original(cleaned);

        string? comPath = TryActiveDocumentFromApps(cleaned);
        if (comPath is not null && File.Exists(comPath))
            return CaptureAttempt.Original(comPath, "Active application document");

        string fileName = Path.GetFileName(cleaned);
        if (string.IsNullOrWhiteSpace(fileName))
            return CaptureAttempt.MetadataOnly("Document title is not a resolvable file name");

        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');

        var namesToTry = BuildNameVariants(fileName).ToList();
        var candidates = new List<string>();

        foreach (string name in namesToTry)
        {
            foreach (string hit in FindByFileName(name, username))
            {
                if (!candidates.Contains(hit, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(hit);
                if (candidates.Count >= 20) break;
            }
            if (candidates.Count >= 20) break;
        }

        foreach (string hit in FindInRecent(namesToTry))
        {
            if (!candidates.Contains(hit, StringComparer.OrdinalIgnoreCase))
                candidates.Add(hit);
        }

        if (candidates.Count == 0)
            return CaptureAttempt.MetadataOnly($"Source not found for '{fileName}'");

        string best = candidates
            .OrderByDescending(ScoreCandidate)
            .ThenByDescending(SafeWriteTime)
            .First();

        string? note = candidates.Count == 1 ? null : $"Resolved among {candidates.Count} matches";
        return CaptureAttempt.Original(best, note);
    }

    public static string CleanDocumentTitle(string name)
    {
        name = name.Trim().Trim('"');
        name = Regex.Replace(name, @"\s*@\s*[\d.,]+%\s*(\([^)]*\))?\s*$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s*\((RGB|CMYK|Lab|Gray)[^)]*\)\s*$", "", RegexOptions.IgnoreCase);

        string[] suffixes =
        [
            " - Word", " — Word", " - Microsoft Word",
            " - Excel", " - Microsoft Excel",
            " - PowerPoint", " - Microsoft PowerPoint",
            " - Adobe Acrobat", " - Adobe Acrobat Reader", " - Adobe Reader",
            " - Adobe Photoshop", " - Photoshop", " - Photoshop.exe",
            " - Paint", " - Paint 3D", " - Notepad",
            " - Microsoft Edge", " - Google Chrome", " - Firefox",
            " - Foxit PDF Reader", " - Foxit Reader",
            " - Photos", " - Windows Photo Viewer"
        ];
        foreach (string suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length].Trim();
        }

        name = Regex.Replace(name, @"\s*\[Compatibility Mode\]\s*$", "", RegexOptions.IgnoreCase);
        return name.Trim();
    }

    private static IEnumerable<string> BuildNameVariants(string fileName)
    {
        yield return fileName;
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);

        if (string.IsNullOrWhiteSpace(ext))
        {
            foreach (string e in GuessExtensions(stem))
                yield return stem + e;
        }
        else if (OfficeExt.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
                 PhotoshopExt.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            yield return stem + ext.ToLowerInvariant();
        }
    }

    private static IEnumerable<string> GuessExtensions(string stem)
    {
        string lower = stem.ToLowerInvariant();
        if (lower.Contains("psd") || lower.Contains("mock") || lower.Contains("design"))
            return PhotoshopExt;
        if (lower.Contains("doc") || lower.Contains("letter") || lower.Contains("report"))
            return OfficeExt;
        return AllHintExt;
    }

    private static IEnumerable<string> FindByFileName(string fileName, string? username)
    {
        foreach (string root in GetSearchRoots(username))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            using var enumerator = SafeEnumerateFiles(root, fileName);
            if (enumerator is null) continue;

            int count = 0;
            while (count < 12)
            {
                bool moved;
                try { moved = enumerator.MoveNext(); }
                catch { break; }
                if (!moved) break;

                yield return enumerator.Current;
                count++;
            }
        }
    }

    private static IEnumerator<string>? SafeEnumerateFiles(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SafeEnumOptions).GetEnumerator();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> FindInRecent(IEnumerable<string> names)
    {
        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        foreach (string recent in GetRecentFolders())
        {
            string[] lnks;
            try { lnks = Directory.GetFiles(recent, "*.lnk"); }
            catch { continue; }

            foreach (string lnk in lnks.OrderByDescending(SafeWriteTime).Take(80))
            {
                string target;
                try { target = ResolveShortcut(lnk); }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(target) || !File.Exists(target)) continue;
                string targetName = Path.GetFileName(target);
                string targetStem = Path.GetFileNameWithoutExtension(target);
                if (nameSet.Contains(targetName) || nameSet.Contains(targetStem))
                    yield return target;
            }
        }
    }

    private static IEnumerable<string> GetRecentFolders()
    {
        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (Directory.Exists(recent))
            yield return recent;

        string users = Environment.GetEnvironmentVariable("SYSTEMDRIVE") is string drive
            ? Path.Combine(drive + Path.DirectorySeparatorChar, "Users")
            : @"C:\Users";
        if (!Directory.Exists(users)) yield break;

        IEnumerable<string> profiles;
        try { profiles = Directory.EnumerateDirectories(users); }
        catch { yield break; }

        foreach (string profile in profiles)
        {
            string leaf = Path.GetFileName(profile);
            if (leaf is "Public" or "Default" or "Default User" or "All Users") continue;
            string userRecent = Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Recent");
            if (Directory.Exists(userRecent))
                yield return userRecent;
        }
    }

    private static string ResolveShortcut(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return "";
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = (string)shortcut.TargetPath;
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            return target ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string? TryActiveDocumentFromApps(string cleanedTitle)
    {
        string? word = TryWordActiveDocument();
        if (word is not null)
        {
            if (TitleMatches(cleanedTitle, word)) return word;
            if (IsGenericWordTitle(cleanedTitle)) return word;
        }

        string? ps = TryPhotoshopActiveDocument();
        if (ps is not null && TitleMatches(cleanedTitle, ps))
            return ps;

        return null;
    }

    private static bool IsGenericWordTitle(string title)
    {
        string stem = Path.GetFileNameWithoutExtension(title);
        return Regex.IsMatch(stem, @"^Document\d*$", RegexOptions.IgnoreCase);
    }

    private static bool TitleMatches(string title, string path)
    {
        string t = Path.GetFileName(title);
        string p = Path.GetFileName(path);
        if (t.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetFileNameWithoutExtension(t)
            .Equals(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryWordActiveDocument()
    {
        try
        {
            object? raw = ComActiveObject.TryGet("Word.Application");
            if (raw is null) return null;
            dynamic word = raw;
            dynamic doc = word.ActiveDocument;
            string path = (string)doc.FullName;
            string name = (string)doc.Name;
            return string.IsNullOrWhiteSpace(path) || path.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? null
                : path;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryPhotoshopActiveDocument()
    {
        try
        {
            object? raw = ComActiveObject.TryGet("Photoshop.Application");
            if (raw is null) return null;
            dynamic app = raw;
            dynamic doc = app.ActiveDocument;
            string path = (string)doc.FullName;
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> GetSearchRoots(string? username)
    {
        // Prefer shallow, real folders — avoid shell namespace junctions under Documents.
        string[] local =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "Documents"),
        ];
        foreach (string root in local.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                yield return root;

        if (string.IsNullOrWhiteSpace(username)) yield break;

        string shortName = username.Contains('\\') ? username.Split('\\').Last() : username;
        string users = Environment.GetEnvironmentVariable("SYSTEMDRIVE") is string drive
            ? Path.Combine(drive + Path.DirectorySeparatorChar, "Users")
            : @"C:\Users";
        string profile = Path.Combine(users, shortName);
        if (!Directory.Exists(profile)) yield break;

        foreach (string sub in new[] { "Documents", "Desktop", "Downloads", "Pictures", "OneDrive" })
        {
            string path = Path.Combine(profile, sub);
            if (Directory.Exists(path))
                yield return path;
        }
    }

    private static long ScoreCandidate(string path)
    {
        long score = 0;
        string ext = Path.GetExtension(path);
        if (OfficeExt.Contains(ext, StringComparer.OrdinalIgnoreCase)) score += 50;
        if (PhotoshopExt.Contains(ext, StringComparer.OrdinalIgnoreCase)) score += 50;
        if (PdfExt.Contains(ext, StringComparer.OrdinalIgnoreCase)) score += 30;
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.FromHours(2)) score += 40;
            else if (age < TimeSpan.FromDays(2)) score += 20;
        }
        catch { /* ignore */ }
        return score;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}

public sealed record CaptureAttempt(CaptureResult Result, string? Path, string StatusText)
{
    public static CaptureAttempt MetadataOnly(string reason) =>
        new(CaptureResult.MetadataOnly, null, $"MetadataOnly - {reason}");

    public static CaptureAttempt Original(string path, string? note = null) =>
        new(CaptureResult.OriginalCaptured, path,
            string.IsNullOrWhiteSpace(note) ? "OriginalCaptured" : $"OriginalCaptured - {note}");
}
