// SPDX-License-Identifier: MIT
using System;
using System.IO;

namespace Ryujinx.Common
{
    /// <summary>
    /// Resolves agent/diagnostic log paths. All nx2 probe output must stay inside
    /// the workspace <c>.nx2-logs</c> directory (AGENTS.md section 9); any path
    /// escaping it is refused instead of created.
    /// </summary>
    public static class Nx2LogConfinement
    {
        public const string LogDirectoryName = ".nx2-logs";
        public const int DefaultCapLines = 50000;

        public static string ResolveWorkspaceRoot()
        {
            string dir = AppContext.BaseDirectory;

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, LogDirectoryName)) ||
                    Directory.Exists(Path.Combine(dir, "Ryubing", "src")))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            throw new InvalidOperationException(
                $"Cannot locate workspace root holding \"{LogDirectoryName}\". " +
                "Refusing to write a log outside the workspace.");
        }

        public static string ResolveLogPath(string fileName, string logDirOverride = null)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                fileName.Contains('/') || fileName.Contains('\\'))
            {
                throw new ArgumentOutOfRangeException(nameof(fileName),
                    $"Refusing log file name \"{fileName}\".");
            }

            string root = ResolveWorkspaceRoot();
            string expectedBase = Path.GetFullPath(Path.Combine(root, LogDirectoryName));

            // The override names a subdirectory of the log directory; absolute values
            // and ".." therefore escape and are refused below.
            string baseDir = string.IsNullOrWhiteSpace(logDirOverride)
                ? expectedBase
                : Path.GetFullPath(Path.Combine(expectedBase, logDirOverride));

            // Only the workspace .nx2-logs tree (or a subdirectory of it) is allowed.
            if (baseDir != expectedBase && !baseDir.StartsWith(expectedBase + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new ArgumentOutOfRangeException(nameof(logDirOverride),
                    $"Refusing log directory \"{logDirOverride}\": outside \"{LogDirectoryName}\".");
            }

            Directory.CreateDirectory(baseDir);

            return Path.Combine(baseDir, fileName);
        }

        public static int ResolveCap(string envValue)
        {
            if (int.TryParse(envValue, out int cap) && cap > 0)
            {
                return cap;
            }

            return DefaultCapLines;
        }
    }
}
