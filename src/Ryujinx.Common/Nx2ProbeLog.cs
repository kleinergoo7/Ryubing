// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Ryujinx.Common
{
    /// <summary>
    /// Capped, env-gated diagnostic probe writer. All probe output is confined to
    /// the workspace <c>.nx2-logs</c> directory (AGENTS.md section 9); each file
    /// stops growing after <c>NX2_CAP</c> lines (default
    /// <see cref="Nx2LogConfinement.DefaultCapLines"/>) with a truncation notice.
    /// </summary>
    public static class Nx2ProbeLog
    {
        private const string CapEnvName = "NX2_CAP";
        private const string DirEnvName = "NX2_LOG_DIR";
        private const string TruncatedNotice = "# truncated: cap reached, further lines dropped";

        private static readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> _lineCounts = new(StringComparer.Ordinal);

        public static bool Enabled(string envName)
        {
            return Environment.GetEnvironmentVariable(envName) == "1";
        }

        public static void Append(string fileName, string line)
        {
            object fileLock = _locks.GetOrAdd(fileName, _ => new object());

            lock (fileLock)
            {
                long cap = Nx2LogConfinement.ResolveCap(Environment.GetEnvironmentVariable(CapEnvName));
                long written = _lineCounts.GetOrAdd(fileName, 0);

                if (written >= cap)
                {
                    return;
                }

                string path = Nx2LogConfinement.ResolveLogPath(fileName, Environment.GetEnvironmentVariable(DirEnvName));

                if (written == 0 && File.Exists(path))
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, line + "\n", Encoding.UTF8);

                if (written + 1 >= cap)
                {
                    File.AppendAllText(path, TruncatedNotice + "\n", Encoding.UTF8);
                }

                _lineCounts[fileName] = written + 1;
            }
        }
    }
}
