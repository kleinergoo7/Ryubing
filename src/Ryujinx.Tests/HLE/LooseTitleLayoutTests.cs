// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.Loaders.Processes;
using System;
using System.IO;

namespace Ryujinx.Tests.HLE
{
    public class LooseTitleLayoutTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            // Isolated per-test folder; removed in TearDown.
            _root = Path.Combine(
                Path.GetTempPath(), "LooseTitleLayoutTests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void MakeExeFs(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "main"), new byte[8]);
            File.WriteAllBytes(Path.Combine(dir, "main.npdm"), new byte[8]);
        }

        [Test]
        public void ExeFsSubfolderResolvesSiblingRomFs()
        {
            // Switch 2 loose dump shape: <title>/exefs + <title>/romfs.
            string title = Path.Combine(_root, "base");

            MakeExeFs(Path.Combine(title, "exefs"));
            Directory.CreateDirectory(Path.Combine(title, "romfs"));

            (string exeFs, string romFs) = LooseTitleLayout.Resolve(title);

            Assert.Multiple(() =>
            {
                Assert.That(exeFs, Is.EqualTo(Path.Combine(title, "exefs")));
                Assert.That(romFs, Is.EqualTo(Path.Combine(title, "romfs")));
            });
        }

        [Test]
        public void ExeFsSubfolderWithoutRomFsReturnsNullRomFs()
        {
            string title = Path.Combine(_root, "base");

            MakeExeFs(Path.Combine(title, "exefs"));

            (string exeFs, string romFs) = LooseTitleLayout.Resolve(title);

            Assert.Multiple(() =>
            {
                Assert.That(exeFs, Is.EqualTo(Path.Combine(title, "exefs")));
                Assert.That(romFs, Is.Null);
            });
        }

        [Test]
        public void FlatRootUsesParentRomFs()
        {
            // Legacy shape: executables at the root, RomFS as a sibling of the root.
            string title = Path.Combine(_root, "title");

            MakeExeFs(title);
            Directory.CreateDirectory(Path.Combine(_root, "romfs"));

            (string exeFs, string romFs) = LooseTitleLayout.Resolve(title);

            Assert.Multiple(() =>
            {
                Assert.That(exeFs, Is.EqualTo(title));
                Assert.That(romFs, Is.EqualTo(Path.Combine(_root, "romfs")));
            });
        }

        [Test]
        public void FlatRootWithoutParentRomFsReturnsNullRomFs()
        {
            string title = Path.Combine(_root, "title");

            MakeExeFs(title);

            (string exeFs, string romFs) = LooseTitleLayout.Resolve(title);

            Assert.Multiple(() =>
            {
                Assert.That(exeFs, Is.EqualTo(title));
                Assert.That(romFs, Is.Null);
            });
        }
    }
}
