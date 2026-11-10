namespace PrintArchive.Core;

/// <summary>
/// Best-effort original-document correlation. Windows print events do not include
/// the source path; this resolver never invents a path from a bare title alone
/// unless a real file is found on disk.
/// </summary>
public static class DocumentCapture
{
    private static readonly string[] SearchRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
    ];

    public static CaptureAttempt TryCapture(string documentName, string? username)
    {
        if (string.IsNullOrWhiteSpace(documentName))
            return CaptureAttempt.MetadataOnly("Empty document name");

        // Case 1: event already contains a full path.
        if (Path.IsPathRooted(documentName) && File.Exists(documentName))
            return CaptureAttempt.Original(documentName);

        // Strip common " - App" suffixes from window titles.
        string cleaned = StripAppSuffix(documentName.Trim());

        if (Path.IsPathRooted(cleaned) && File.Exists(cleaned))
            return CaptureAttempt.Original(cleaned);

        // Case 2: look for an exact file name under common user folders.
        string fileName = Path.GetFileName(cleaned);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return CaptureAttempt.MetadataOnly("Document title is not a resolvable file name");

        var candidates = new List<string>();
        foreach (string root in GetSearchRoots(username))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string hit in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                             .Take(8))
                {
                    candidates.Add(hit);
                }
            }
            catch
            {
                // Access denied / junction loops — skip root.
            }
        }

        if (candidates.Count == 1)
            return CaptureAttempt.Original(candidates[0]);

        if (candidates.Count > 1)
        {
            // Prefer the most recently written match.
            string best = candidates.OrderByDescending(p => SafeWriteTime(p)).First();
            return CaptureAttempt.Original(best, $"Resolved among {candidates.Count} matches");
        }

        return CaptureAttempt.MetadataOnly("Source file path not found");
    }

    private static IEnumerable<string> GetSearchRoots(string? username)
    {
        foreach (string root in SearchRoots)
            if (!string.IsNullOrWhiteSpace(root))
                yield return root;

        // Service running as SYSTEM: also scan profile of the printing user.
        if (!string.IsNullOrWhiteSpace(username))
        {
            string shortName = username.Contains('\\') ? username.Split('\\').Last() : username;
            string users = Environment.GetEnvironmentVariable("SYSTEMDRIVE") is string drive
                ? Path.Combine(drive + Path.DirectorySeparatorChar, "Users")
                : @"C:\Users";
            string profile = Path.Combine(users, shortName);
            if (Directory.Exists(profile))
            {
                yield return Path.Combine(profile, "Documents");
                yield return Path.Combine(profile, "Desktop");
                yield return Path.Combine(profile, "Downloads");
                yield return Path.Combine(profile, "OneDrive");
            }
        }
    }

    private static string StripAppSuffix(string name)
    {
        string[] suffixes =
        [
            " - Word", " - Excel", " - PowerPoint", " - Adobe Acrobat", " - Adobe Reader",
            " - Microsoft Edge", " - Google Chrome", " - Paint", " - Notepad",
            " - Photoshop", " - Adobe Photoshop", " - Foxit PDF Reader"
        ];
        foreach (string suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length].Trim();
        }
        return name;
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
        new(CaptureResult.MetadataOnly, null, $"MetadataOnly — {reason}");

    public static CaptureAttempt Original(string path, string? note = null) =>
        new(CaptureResult.OriginalCaptured, path,
            string.IsNullOrWhiteSpace(note) ? "OriginalCaptured" : $"OriginalCaptured — {note}");
}
