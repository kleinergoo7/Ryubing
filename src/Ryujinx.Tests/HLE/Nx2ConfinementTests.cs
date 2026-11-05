// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Common;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using System;
using System.IO;

namespace Ryujinx.Tests.HLE
{
    public class Nx2ConfinementTests
    {
        [Test]
        public void LogPathDefaultsInsideWorkspaceNx2Logs()
        {
            // Confinement rule (AGENTS.md section 9): probe output stays in .nx2-logs.
            string path = Nx2LogConfinement.ResolveLogPath("nx2-tipc.log");

            Assert.That(path, Does.EndWith(Path.Combine(".nx2-logs", "nx2-tipc.log")));
            Assert.That(Path.GetFullPath(path), Does.StartWith(
                Path.GetFullPath(Nx2LogConfinement.ResolveWorkspaceRoot()) + Path.DirectorySeparatorChar));
        }

        [Test]
        public void LogPathRefusesEscapeOutsideNx2Logs()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Nx2LogConfinement.ResolveLogPath("x.log", ".."));
            Assert.Throws<ArgumentOutOfRangeException>(() => Nx2LogConfinement.ResolveLogPath("x.log", "/tmp"));
            Assert.Throws<ArgumentOutOfRangeException>(() => Nx2LogConfinement.ResolveLogPath("../evil.log"));
            Assert.Throws<ArgumentOutOfRangeException>(() => Nx2LogConfinement.ResolveLogPath("sub/dir.log"));
        }

        [Test]
        public void LogCapFallsBackToDefault()
        {
            Assert.That(Nx2LogConfinement.ResolveCap(null), Is.EqualTo(Nx2LogConfinement.DefaultCapLines));
            Assert.That(Nx2LogConfinement.ResolveCap("nope"), Is.EqualTo(Nx2LogConfinement.DefaultCapLines));
            Assert.That(Nx2LogConfinement.ResolveCap("100"), Is.EqualTo(100));
        }

        [Test]
        public void Nx2InfoTypeIdsAreStable()
        {
            // Guessed-layout WORKAROUND in Syscall.cs branches on these exact ids;
            // a renumber without updating the handler would silently misroute queries.
            Assert.That((uint)InfoType.Nx2RegionAddress, Is.EqualTo(29u));
            Assert.That((uint)InfoType.Nx2RegionSize, Is.EqualTo(30u));
            Assert.That((uint)InfoType.Nx2RemoteMemorySize, Is.EqualTo(31u));
            Assert.That((uint)InfoType.Nx2RemoteMemoryUsed, Is.EqualTo(32u));
            Assert.That((uint)InfoType.Nx2VammSize, Is.EqualTo(33u));
        }

        [Test]
        public void MemoryConfiguration12GiBIsTwelveGibibytes()
        {
            // Switch 2 DRAM target: 12 GiB (9 GB games / 3 GB OS reserve per DF specs).
            const ulong GiB = 1024UL * 1024UL * 1024UL;

            Assert.That(MemoryConfiguration.MemoryConfiguration12GiB.DramSize, Is.EqualTo(12UL * GiB));
        }

        [Test]
        public void ProbeLogStopsAtCapWithNotice()
        {
            // Capped probe (AGENTS.md section 9): stops growing after NX2_CAP lines.
            // Written file is deleted after the test; nothing persists outside .nx2-logs.
            string fileName = $"nx2-test-{Guid.NewGuid():N}.log";
            string previousCap = Environment.GetEnvironmentVariable("NX2_CAP");

            try
            {
                Environment.SetEnvironmentVariable("NX2_CAP", "3");

                for (int i = 1; i <= 5; i++)
                {
                    Nx2ProbeLog.Append(fileName, $"line{i}");
                }

                string path = Nx2LogConfinement.ResolveLogPath(fileName);
                string[] lines = File.ReadAllLines(path);

                Assert.That(lines, Is.EqualTo(new[] { "line1", "line2", "line3", "# truncated: cap reached, further lines dropped" }));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NX2_CAP", previousCap);
                File.Delete(Nx2LogConfinement.ResolveLogPath(fileName));
            }
        }

        [Test]
        public void ProbeLogHonorsLogDirOverrideInsideNx2Logs()
        {
            // NX2_LOG_DIR may select a subdirectory of .nx2-logs, nothing else.
            string subDir = $"nx2-sub-{Guid.NewGuid():N}";
            string fileName = "nx2-dir.log";
            string previousDir = Environment.GetEnvironmentVariable("NX2_LOG_DIR");

            try
            {
                Environment.SetEnvironmentVariable("NX2_LOG_DIR", subDir);
                Nx2ProbeLog.Append(fileName, "hello");

                string expected = Nx2LogConfinement.ResolveLogPath(fileName, subDir);

                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(expected), Is.True);
                    Assert.That(File.ReadAllText(expected).Trim(), Is.EqualTo("hello"));
                    Assert.That(expected, Does.Contain(subDir));
                });

                File.Delete(expected);
                Directory.Delete(Path.GetDirectoryName(expected));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NX2_LOG_DIR", previousDir);
                Nx2LogConfinement.ResolveLogPath(fileName); // no-op; keeps default path callable
            }
        }

        [Test]
        public void ProbeLogRefusesLogDirEscapingWorkspace()
        {
            string previousDir = Environment.GetEnvironmentVariable("NX2_LOG_DIR");

            try
            {
                Environment.SetEnvironmentVariable("NX2_LOG_DIR", "..");

                Assert.Throws<ArgumentOutOfRangeException>(() => Nx2ProbeLog.Append("nx2-escape.log", "x"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NX2_LOG_DIR", previousDir);
            }
        }
    }
}
