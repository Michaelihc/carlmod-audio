using System;
using System.Collections.Generic;
using System.IO;

namespace CarlModAudio.Internal;

/// <summary>The audio folder: resolving file names in it and listing it.</summary>
internal static class AudioFiles
{
    private static readonly string[] Extensions = [".ogg", ".wav"];

    public static string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Resolves a file name to a full path. Names are relative to the audio folder and may omit the extension.
    /// Without <paramref name="allowOutsideFolder"/> (commands), paths that leave the folder are refused.
    /// </summary>
    public static string? Resolve(string name, bool allowOutsideFolder, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "no file name given";
            return null;
        }

        string path;
        try
        {
            path = Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(Folder, name));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"invalid file name '{name}'";
            return null;
        }

        if (!allowOutsideFolder && !IsInFolder(path))
        {
            error = $"'{name}' is outside the audio folder";
            return null;
        }

        if (File.Exists(path))
            return path;

        if (!Path.HasExtension(path))
        {
            foreach (string extension in Extensions)
            {
                if (File.Exists(path + extension))
                    return path + extension;
            }
        }

        error = $"file '{name}' not found in {Folder}";
        return null;
    }

    /// <summary>Audio files in the folder (and its subfolders), as paths relative to it.</summary>
    public static List<string> List()
    {
        var files = new List<string>();
        if (!Directory.Exists(Folder))
            return files;

        string root = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (Array.FindIndex(Extensions, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)) >= 0)
                files.Add(file.Substring(root.Length));
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static bool IsInFolder(string fullPath)
    {
        string root = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
